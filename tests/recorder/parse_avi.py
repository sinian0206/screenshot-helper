#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""RIFF AVI（MJPEG）结构解析与校验工具。

只依赖 Python 标准库；用于验证 src/AviWriter.cs 生成的文件是否满足标准 AVI 结构：
  RIFF/AVI -> LIST hdrl (avih + LIST strl (strh + strf)) -> LIST movi (00dc...) -> idx1

校验项：
  * RIFF size 与文件实际大小一致（len - 8）
  * avih：宽/高/总帧数/流数/AVIF_HASINDEX 标志
  * strh：fccType=vids、fccHandler=MJPG、rate/scale 帧率、dwLength=总帧数、rcFrame
  * strf：biCompression=MJPG、biWidth/biHeight 与 avih 一致、biBitCount=24
  * movi：00dc 帧数量 = avih.dwTotalFrames = strh.dwLength = idx1 条目数
  * idx1：每条 offset 指向对应 00dc chunk 头且 ckid/flags 正确；帧按顺序全部被索引
  * 每帧 JPEG 数据以 FFD8 开始、FFD9 结束
  * movi/hdrl/strl LIST 长度与实际数据一致
  * 若系统有 ffprobe，则额外用 ffprobe 交叉验证 codec/尺寸/帧数/时长（没有则跳过）

用法：
  python tests/recorder/parse_avi.py <file.avi> [--expect-frames N] [--expect-size WxH]
        [--expect-fps F] [--min-seconds X] [--json]
退出码：0 = 全部通过；1 = 存在失败；2 = 文件不存在/无法解析。
"""
import json
import os
import shutil
import struct
import subprocess
import sys


class ParseError(Exception):
    pass


def u32(data, pos):
    if pos + 4 > len(data):
        raise ParseError("越界读取 u32 @%d" % pos)
    return struct.unpack_from("<I", data, pos)[0]


def u16(data, pos):
    if pos + 2 > len(data):
        raise ParseError("越界读取 u16 @%d" % pos)
    return struct.unpack_from("<H", data, pos)[0]


def fourcc(data, pos):
    if pos + 4 > len(data):
        raise ParseError("越界读取 FourCC @%d" % pos)
    return data[pos:pos + 4].decode("ascii", "replace")


def parse_avi(path):
    with open(path, "rb") as f:
        data = f.read()
    info = {
        "file": os.path.abspath(path),
        "file_size": len(data),
        "errors": [],
        "warnings": [],
    }

    def err(msg):
        info["errors"].append(msg)

    def warn(msg):
        info["warnings"].append(msg)

    if len(data) < 12:
        raise ParseError("文件过小")
    if data[0:4] != b"RIFF":
        raise ParseError("不是 RIFF 文件")
    riff_size = u32(data, 4)
    if data[8:12] != b"AVI ":
        raise ParseError("RIFF 类型不是 AVI ")
    if riff_size != len(data) - 8:
        err("RIFF size 字段 %d != 文件大小-8 %d" % (riff_size, len(data) - 8))

    info["riff_size"] = riff_size
    info["has_hdrl"] = False
    info["has_movi"] = False
    info["frame_offsets"] = []
    info["frame_sizes"] = []
    info["idx_entries"] = 0
    info["movi_fourcc_pos"] = None
    info["idx_flag_summary"] = {}
    info["_top_level"] = []

    pos = 12
    while pos + 8 <= len(data):
        tag = fourcc(data, pos)
        size = u32(data, pos + 4)
        body = pos + 8
        if body + size > len(data):
            err("顶层 chunk %s 长度越界（pos=%d size=%d）" % (tag, pos, size))
            break
        info["_top_level"].append(tag)
        if tag == "LIST":
            list_type = fourcc(data, body)
            info["_top_level"].append("LIST:" + list_type)
            if list_type == "hdrl":
                info["has_hdrl"] = True
                parse_hdrl(data, body + 4, body + size, info)
            elif list_type == "movi":
                info["has_movi"] = True
                info["movi_fourcc_pos"] = body
                parse_movi(data, body + 4, body + size, info)
            else:
                warn("未知 LIST 类型: " + list_type)
        elif tag == "idx1":
            parse_idx1(data, body, size, info)
        else:
            warn("未知顶层 chunk: " + tag)
        pos = body + size
        if size & 1:
            pos += 1  # chunk 偶数字节对齐
    if pos != len(data):
        warn("顶层 chunk 遍历结束位置 %d != 文件大小 %d（可能是尾部填充）" % (pos, len(data)))

    validate(info)
    return info


def parse_hdrl(data, start, end, info):
    pos = start
    while pos + 8 <= end:
        tag = fourcc(data, pos)
        size = u32(data, pos + 4)
        body = pos + 8
        if body + size > end:
            info["errors"].append("hdrl 内 chunk %s 长度越界" % tag)
            break
        if tag == "avih":
            if size < 56:
                info["errors"].append("avih 数据长度 %d < 56" % size)
            info["avih"] = {
                "micro_sec_per_frame": u32(data, body),
                "max_bytes_per_sec": u32(data, body + 4),
                "flags": u32(data, body + 12),
                "total_frames": u32(data, body + 16),
                "streams": u32(data, body + 24),
                "suggested_buffer": u32(data, body + 28),
                "width": u32(data, body + 32),
                "height": u32(data, body + 36),
            }
        elif tag == "LIST" and fourcc(data, body) == "strl":
            parse_strl(data, body + 4, body + size, info)
        else:
            info["warnings"].append("hdrl 内忽略 chunk: " + tag)
        pos = body + size
        if size & 1:
            pos += 1


def parse_strl(data, start, end, info):
    pos = start
    while pos + 8 <= end:
        tag = fourcc(data, pos)
        size = u32(data, pos + 4)
        body = pos + 8
        if body + size > end:
            info["errors"].append("strl 内 chunk %s 长度越界" % tag)
            break
        if tag == "strh":
            if size < 56:
                info["errors"].append("strh 数据长度 %d < 56" % size)
            info["strh"] = {
                "fcc_type": fourcc(data, body),
                "fcc_handler": fourcc(data, body + 4),
                "flags": u32(data, body + 8),
                "scale": u32(data, body + 20),
                "rate": u32(data, body + 24),
                "length": u32(data, body + 32),
                "suggested_buffer": u32(data, body + 36),
                "rc_frame": [u16(data, body + 48), u16(data, body + 50),
                             u16(data, body + 52), u16(data, body + 54)],
            }
        elif tag == "strf":
            info["strf"] = {
                "bi_size": u32(data, body),
                "width": struct.unpack_from("<i", data, body + 4)[0],
                "height": struct.unpack_from("<i", data, body + 8)[0],
                "bit_count": u16(data, body + 14),
                "compression": fourcc(data, body + 16),
            }
        else:
            info["warnings"].append("strl 内忽略 chunk: " + tag)
        pos = body + size
        if size & 1:
            pos += 1


def parse_movi(data, start, end, info):
    """start = 第一个帧 chunk 头位置；end = movi LIST 数据结束位置。"""
    pos = start
    while pos + 8 <= end:
        tag = fourcc(data, pos)
        size = u32(data, pos + 4)
        body = pos + 8
        if body + size > end:
            info["errors"].append("movi 内 chunk %s 长度越界（pos=%d size=%d）" % (tag, pos, size))
            break
        info["frame_offsets"].append(pos - info["movi_fourcc_pos"])
        info["frame_sizes"].append(size)
        info["frame_tags"] = info.get("frame_tags", [])
        info["frame_tags"].append(tag)
        if tag != "00dc":
            info["warnings"].append("非视频 chunk: %s（offset %d）" % (tag, pos))
        if size >= 2:
            if data[body:body + 2] != b"\xff\xd8":
                info["warnings"].append("帧 @%d 未以 JPEG SOI(FFD8) 开始" % len(info["frame_sizes"]))
            if data[body + size - 2:body + size] != b"\xff\xd9":
                info["warnings"].append("帧 @%d 未以 JPEG EOI(FFD9) 结束" % len(info["frame_sizes"]))
        pos = body + size
        if size & 1:
            pos += 1  # chunk 填充


def parse_idx1(data, body, size, info):
    if size % 16 != 0:
        info["errors"].append("idx1 长度 %d 不是 16 的倍数" % size)
    count = size // 16
    info["idx_entries"] = count
    info["idx"] = []
    flags_summary = {}
    for i in range(count):
        p = body + i * 16
        ckid = fourcc(data, p)
        flags = u32(data, p + 4)
        offset = u32(data, p + 8)
        chunk_len = u32(data, p + 12)
        flags_summary[flags] = flags_summary.get(flags, 0) + 1
        info["idx"].append({"ckid": ckid, "flags": flags, "offset": offset, "length": chunk_len})
    info["idx_flag_summary"] = flags_summary


def validate(info):
    avih = info.get("avih") or {}
    strh = info.get("strh") or {}
    strf = info.get("strf") or {}
    errors = info["errors"]

    if not info["has_hdrl"]:
        errors.append("缺少 LIST hdrl")
    if not info["has_movi"]:
        errors.append("缺少 LIST movi")
    if avih and strh and strf:
        if avih.get("streams") != 1:
            errors.append("avih.dwStreams=%s，期望 1" % avih.get("streams"))
        if not (avih.get("flags", 0) & 0x10):
            errors.append("avih.dwFlags 未设置 AVIF_HASINDEX(0x10)")
        if strh.get("fcc_type") != "vids":
            errors.append("strh.fccType=%r，期望 'vids'" % strh.get("fcc_type"))
        if strh.get("fcc_handler") != "MJPG":
            errors.append("strh.fccHandler=%r，期望 'MJPG'" % strh.get("fcc_handler"))
        if strh.get("scale") in (0, None):
            errors.append("strh.dwScale 为 0")
        elif avih.get("width") and strf.get("width") != avih.get("width"):
            errors.append("strf.biWidth=%s 与 avih.dwWidth=%s 不一致" %
                          (strf.get("width"), avih.get("width")))
        if avih.get("height") and strf.get("height") != avih.get("height"):
            errors.append("strf.biHeight=%s 与 avih.dwHeight=%s 不一致" %
                          (strf.get("height"), avih.get("height")))
        if strf.get("compression") != "MJPG":
            errors.append("strf.biCompression=%r，期望 'MJPG'" % strf.get("compression"))
        if strf.get("bit_count") != 24:
            errors.append("strf.biBitCount=%s，期望 24" % strf.get("bit_count"))
        if strf.get("bi_size") != 40:
            errors.append("strf.biSize=%s，期望 40" % strf.get("bi_size"))
        if strh.get("rc_frame") and avih.get("width") and avih.get("height"):
            rc = strh["rc_frame"]
            if rc[2] != avih["width"] or rc[3] != avih["height"]:
                errors.append("strh.rcFrame=%s 与 avih 尺寸不一致" % (rc,))
        if avih.get("width") in (0, None) or avih.get("height") in (0, None):
            errors.append("avih 宽高为 0")
        if avih.get("width") and avih["width"] % 2:
            errors.append("视频宽度为奇数 %s（MJPEG 应为偶数）" % avih["width"])
        if avih.get("height") and avih["height"] % 2:
            errors.append("视频高度为奇数 %s（MJPEG 应为偶数）" % avih["height"])

        movi_count = len(info["frame_offsets"])
        if avih.get("total_frames") != movi_count:
            errors.append("avih.dwTotalFrames=%s != movi 实际帧数 %s" %
                          (avih.get("total_frames"), movi_count))
        if strh.get("length") != movi_count:
            errors.append("strh.dwLength=%s != movi 实际帧数 %s" %
                          (strh.get("length"), movi_count))
        if info["idx_entries"] != movi_count:
            errors.append("idx1 条目数 %s != movi 实际帧数 %s" % (info["idx_entries"], movi_count))
        if movi_count > 0 and strh.get("suggested_buffer", 0) < max(info["frame_sizes"]):
            info["warnings"].append("strh.dwSuggestedBufferSize 小于最大帧")
        # idx 校验
        for i, entry in enumerate(info.get("idx", [])):
            if i >= movi_count:
                break
            if entry["ckid"] != "00dc":
                errors.append("idx1[%d].ckid=%r != '00dc'" % (i, entry["ckid"]))
            if entry["offset"] != info["frame_offsets"][i]:
                errors.append("idx1[%d].offset=%d != 实际帧偏移 %d" %
                              (i, entry["offset"], info["frame_offsets"][i]))
            if entry.get("length") != info["frame_sizes"][i]:
                errors.append("idx1[%d].dwChunkLength=%s != 实际帧大小 %d" %
                              (i, entry.get("length"), info["frame_sizes"][i]))
            if not (entry["flags"] & 0x10):
                errors.append("idx1[%d] 未标记关键帧(0x10)" % i)
        if movi_count > 0:
            info["duration_sec"] = movi_count * float(strh.get("scale") or 1) / float(strh.get("rate") or 1)
            info["fps"] = float(strh.get("rate") or 1) / float(strh.get("scale") or 1)
        else:
            info["duration_sec"] = 0.0
            info["fps"] = 0.0
    else:
        errors.append("缺少 avih/strh/strf 头部")

    # 可选的 ffprobe 交叉验证
    ffprobe = shutil.which("ffprobe")
    if ffprobe:
        try:
            out = subprocess.check_output([
                ffprobe, "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=codec_name,width,height,nb_frames,r_frame_rate",
                "-of", "json", info["file"]], stderr=subprocess.STDOUT, timeout=30)
            stream = (json.loads(out.decode("utf-8", "replace")).get("streams") or [{}])[0]
            info["ffprobe"] = stream
            if stream.get("codec_name") != "mjpeg":
                errors.append("ffprobe codec=%r，期望 mjpeg" % stream.get("codec_name"))
            if avih and stream.get("width") != avih.get("width"):
                errors.append("ffprobe 宽度 %s != avih 宽度 %s" %
                              (stream.get("width"), avih.get("width")))
            if avih and stream.get("height") != avih.get("height"):
                errors.append("ffprobe 高度 %s != avih 高度 %s" %
                              (stream.get("height"), avih.get("height")))
        except Exception as ex:  # noqa: BLE001
            info["warnings"].append("ffprobe 交叉验证失败: %s" % ex)
    else:
        info["ffprobe"] = None
        info["warnings"].append("系统未安装 ffprobe，跳过交叉验证")


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    path = argv[1]
    expect_frames = None
    expect_size = None
    expect_fps = None
    min_seconds = None
    as_json = False
    i = 2
    while i < len(argv):
        a = argv[i]
        if a == "--expect-frames" and i + 1 < len(argv):
            expect_frames = int(argv[i + 1]); i += 2
        elif a == "--expect-size" and i + 1 < len(argv):
            expect_size = argv[i + 1].lower(); i += 2
        elif a == "--expect-fps" and i + 1 < len(argv):
            expect_fps = float(argv[i + 1]); i += 2
        elif a == "--min-seconds" and i + 1 < len(argv):
            min_seconds = float(argv[i + 1]); i += 2
        elif a == "--json":
            as_json = True; i += 1
        else:
            print("未知参数: " + a)
            return 2

    if not os.path.isfile(path):
        print("FAIL 文件不存在: " + path)
        return 2
    try:
        info = parse_avi(path)
    except ParseError as ex:
        print("FAIL 解析失败: %s" % ex)
        return 2

    if expect_frames is not None and len(info["frame_offsets"]) != expect_frames:
        info["errors"].append("期望帧数 %d，实际 %d" % (expect_frames, len(info["frame_offsets"])))
    if expect_size:
        try:
            w, h = expect_size.replace("×", "x").split("x")
            avih = info.get("avih") or {}
            if avih.get("width") != int(w) or avih.get("height") != int(h):
                info["errors"].append("期望尺寸 %sx%s，实际 %sx%s" %
                                      (w, h, avih.get("width"), avih.get("height")))
        except ValueError:
            print("FAIL --expect-size 参数格式应为 WxH")
            return 2
    if expect_fps is not None and abs(info.get("fps", 0) - expect_fps) > 0.01:
        info["errors"].append("期望帧率 %s，实际 %s" % (expect_fps, info.get("fps")))
    if min_seconds is not None and info.get("duration_sec", 0) + 1e-9 < min_seconds:
        info["errors"].append("期望时长 >= %.2fs，实际 %.3fs" %
                              (min_seconds, info.get("duration_sec", 0)))

    if as_json:
        info.pop("_top_level", None)
        print(json.dumps(info, ensure_ascii=False, indent=2))
    else:
        print_verbose(info)

    if info["errors"]:
        print("\n结果: FAIL（%d 项错误）" % len(info["errors"]))
        for e in info["errors"]:
            print("  [FAIL] " + e)
        return 1
    print("\n结果: PASS")
    return 0


def print_verbose(info):
    avih = info.get("avih") or {}
    strh = info.get("strh") or {}
    strf = info.get("strf") or {}
    print("=" * 62)
    print("文件: %s" % info["file"])
    print("大小: %d 字节（%.1f KB）  RIFF size: %d" %
          (info["file_size"], info["file_size"] / 1024.0, info["riff_size"]))
    print("顶层: %s" % " / ".join(info.get("_top_level", [])))
    print("编码: %s  尺寸: %sx%s  帧率: %s  帧数: %d  时长: %.3f 秒" % (
        strf.get("compression"), avih.get("width"), avih.get("height"),
        info.get("fps"), len(info["frame_offsets"]), info.get("duration_sec", 0)))
    print("avih: total_frames=%s streams=%s flags=0x%X buffer=%s" % (
        avih.get("total_frames"), avih.get("streams"), avih.get("flags", 0),
        avih.get("suggested_buffer")))
    print("strh: type=%s handler=%s scale=%s rate=%s length=%s rcFrame=%s" % (
        strh.get("fcc_type"), strh.get("fcc_handler"), strh.get("scale"),
        strh.get("rate"), strh.get("length"), strh.get("rc_frame")))
    sizes = info["frame_sizes"]
    if sizes:
        print("帧 chunk: %d 个，JPEG 大小 min=%d max=%d avg=%.0f 字节" %
              (len(sizes), min(sizes), max(sizes), sum(sizes) / float(len(sizes))))
    print("idx1: %d 条，flags=%s，offset 全部指向对应 00dc：%s" % (
        info["idx_entries"], info.get("idx_flag_summary"),
        "是" if not info["errors"] else "见错误"))
    print("ffprobe: %s" % ("未安装" if not info.get("ffprobe") else json.dumps(info["ffprobe"], ensure_ascii=False)))
    for w in info["warnings"]:
        print("  [WARN] " + w)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
