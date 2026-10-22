#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用 OpenCV（内置 FFmpeg 解码器）独立验证 AVI 是否真正可解码播放。

parse_avi.py 校验的是字节结构；本脚本从“播放器视角”打开文件、按帧解码，
验证：
  * 文件能被 FFmpeg/OpenCV 识别为视频；
  * 容器 fps / 帧数 / 宽高与录制配置一致；
  * 每一帧都能成功解码成图像（不是坏 JPEG / 坏索引）；
  * 帧图像尺寸 = 视频尺寸，且画面不是全黑（说明抓到的是真实桌面）；
  * （可选 --expect-motion）帧间差异足够大，证明画面内容确实在变化。

用法：
  python tests/recorder/verify_playback_cv2.py <file.avi> [--expect-fps 15]
        [--expect-size WxH] [--expect-frames N] [--expect-motion]
退出码：0 = 通过；1 = 失败；2 = 文件不存在或无 OpenCV。
"""
import os
import sys


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    path = argv[1]
    expect_fps = None
    expect_size = None
    expect_frames = None
    expect_motion = False
    i = 2
    while i < len(argv):
        a = argv[i]
        if a == "--expect-fps" and i + 1 < len(argv):
            expect_fps = float(argv[i + 1]); i += 2
        elif a == "--expect-size" and i + 1 < len(argv):
            expect_size = argv[i + 1].lower().replace("×", "x"); i += 2
        elif a == "--expect-frames" and i + 1 < len(argv):
            expect_frames = int(argv[i + 1]); i += 2
        elif a == "--expect-motion":
            expect_motion = True; i += 1
        else:
            print("未知参数: " + a)
            return 2

    if not os.path.isfile(path):
        print("FAIL 文件不存在: " + path)
        return 2
    try:
        import cv2  # noqa: PLC0415
    except ImportError:
        print("SKIP 未安装 OpenCV（pip install opencv-python），无法做播放器视角验证")
        return 2

    cap = cv2.VideoCapture(path)
    if not cap.isOpened():
        print("FAIL OpenCV 无法打开视频（容器/索引可能损坏）: " + path)
        return 1

    problems = []
    fps = cap.get(cv2.CAP_PROP_FPS)
    reported_count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))

    decoded = 0
    mean_diff_total = 0.0
    motion_frames = 0
    prev_small = None
    all_black = True
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        decoded += 1
        if frame is None or frame.size == 0:
            problems.append("第 %d 帧解码为空" % decoded)
            continue
        if frame.shape[1] != width or frame.shape[0] != height:
            problems.append("第 %d 帧尺寸 %dx%d != 视频尺寸 %dx%d" %
                            (decoded, frame.shape[1], frame.shape[0], width, height))
        if frame.max() > 8:
            all_black = False
        small = cv2.resize(frame, (64, 36))
        if prev_small is not None:
            import numpy as np
            diff = float(np.mean(cv2.absdiff(small, prev_small)))
            mean_diff_total += diff
            if diff > 1.0:
                motion_frames += 1
        prev_small = small
    cap.release()

    avg_diff = mean_diff_total / max(1, decoded - 1)
    print("文件: %s" % os.path.abspath(path))
    print("OpenCV 解码: fps=%.3f 报告帧数=%d 实际解码帧数=%d 尺寸=%dx%d" %
          (fps, reported_count, decoded, width, height))
    print("画面: 平均帧间差异=%.2f（>1.0 视为有变化），有变化帧对数=%d/%d，全黑=%s" %
          (avg_diff, motion_frames, max(0, decoded - 1), all_black))

    if decoded <= 0:
        problems.append("没有任何一帧能解码")
    if all_black:
        problems.append("所有帧全黑，可能没有真正抓到屏幕画面")
    if expect_fps is not None and abs(fps - expect_fps) > 0.1:
        problems.append("OpenCV fps=%.3f 与期望 %.1f 不符" % (fps, expect_fps))
    if expect_size:
        try:
            w, h = expect_size.split("x")
            if width != int(w) or height != int(h):
                problems.append("尺寸 %dx%d 与期望 %s 不符" % (width, height, expect_size))
        except ValueError:
            print("FAIL --expect-size 参数格式应为 WxH")
            return 2
    if expect_frames is not None and decoded != expect_frames:
        problems.append("解码帧数 %d 与期望 %d 不符" % (decoded, expect_frames))
    if expect_motion and motion_frames < max(2, decoded // 10):
        problems.append("帧间差异过小（有变化帧对 %d/%d），画面可能没有实际内容变化" %
                        (motion_frames, max(1, decoded - 1)))

    if problems:
        print("\n结果: FAIL")
        for p in problems:
            print("  [FAIL] " + p)
        return 1
    print("\n结果: PASS（容器可打开、逐帧可解码、画面有效）")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
