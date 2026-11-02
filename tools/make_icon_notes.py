#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成顾客名册图标（记事本样式）：Textures/icon_notes.png

同族图标 (icon_wrench / icon_gear / icon_menu) 都是 128x128 RGBA、透明底、硬边 1-bit alpha、
黑色描边 + 内部留白，所以这里几何用带符号距离场在 4 倍超采样上算，出图时按多数票二值化 alpha，
不抗锯齿、不引 PIL。图标是随构建部署的资产，改样式就改脚本重跑，不在编辑器里手点。
"""

import math
import struct
import zlib
from pathlib import Path

SS = 4
SIZE = 128
BIG = SIZE * SS
STROKE = 7.0
BLACK = (24, 24, 26)
WHITE = (246, 246, 244)

ALPHA = bytearray(BIG * BIG)
COLOR = bytearray(BIG * BIG * 3)


def paint(x, y, rgb, cov):
    if cov <= 0:
        return
    p = y * BIG + x
    dst = ALPHA[p] / 255.0
    out = cov + dst * (1 - cov)
    ci = p * 3
    for c in range(3):
        v = (rgb[c] / 255.0 * cov + COLOR[ci + c] / 255.0 * dst * (1 - cov)) / out if out > 0 else 0
        COLOR[ci + c] = int(v * 255)
    ALPHA[p] = int(out * 255)


def shape(sdf, rgb, stroke=None, box=None):
    """sdf<0 在形状内。stroke=None 填实；给值则只画宽为 stroke 的描边。
    覆盖率 = 0.5 与距离的差，边缘因此有 1 像素过渡，降采样后再二值化。"""
    x0, y0, x1, y1 = box or (0, 0, BIG, BIG)
    for y in range(max(0, y0), min(BIG, y1)):
        for x in range(max(0, x0), min(BIG, x1)):
            d = sdf(x, y)
            cov = (0.5 - d) if stroke is None else (stroke / 2.0 + 0.5 - abs(d))
            cov = 0.0 if cov < 0 else (1.0 if cov > 1 else cov)
            if cov > 0:
                paint(x, y, rgb, cov)


def round_rect_sdf(l, t, r, b, rad):
    hw, hh = (r - l) / 2.0, (b - t) / 2.0
    cx, cy = l + hw, t + hh

    def sdf(x, y):
        qx = abs(x - cx) - hw + rad
        qy = abs(y - cy) - hh + rad
        return math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - rad
    return sdf


def circle_sdf(ccx, ccy, rad):
    def sdf(x, y):
        return math.hypot(x - ccx, y - ccy) - rad
    return sdf


def downsample():
    """盒式降采样 + alpha 多数票二值化：同族图标没有半透明边，留灰边在 Point 过滤下发脏。"""
    out = bytearray(SIZE * SIZE * 4)
    n = SS * SS
    for y in range(SIZE):
        for x in range(SIZE):
            hit = 0
            sums = [0, 0, 0]
            tot = 0
            for sy in range(SS):
                for sx in range(SS):
                    p = (y * SS + sy) * BIG + x * SS + sx
                    a = ALPHA[p]
                    if a:
                        hit += 1
                        tot += a
                        for c in range(3):
                            sums[c] += COLOR[p * 3 + c] * a
            o = (y * SIZE + x) * 4
            if hit * 2 >= n and tot > 0:
                for c in range(3):
                    out[o + c] = min(255, sums[c] // tot)
                out[o + 3] = 255
    return bytes(out)


def write_png(path, raw):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    scan = b"".join(b"\x00" + raw[y * SIZE * 4:(y + 1) * SIZE * 4] for y in range(SIZE))
    Path(path).write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(scan, 9))
        + chunk(b"IEND", b""))


def main():
    s = float(SS)
    stroke = STROKE * s

    # 纸面：留白 + 黑描边
    page = (28 * s, 38 * s, 100 * s, 108 * s, 6 * s)
    shape(round_rect_sdf(*page), WHITE)
    shape(round_rect_sdf(*page), BLACK, stroke=stroke)

    # 装订环：骑在上边缘的 4 个圈，圈心压着纸边、彼此留缝，糊成一坨就读不出是环
    for i in range(4):
        cx = (40 + i * 18) * s
        shape(circle_sdf(cx, 38 * s, 8 * s), BLACK, stroke=6.0 * s)

    # 三行字：最后一行短一点，像写过东西的记事本
    for y, x1 in [(64, 88), (80, 88), (96, 74)]:
        shape(round_rect_sdf(40 * s, (y - STROKE / 2) * s, x1 * s, (y + STROKE / 2) * s, STROKE / 2 * s), BLACK)

    out = Path(__file__).resolve().parents[1] / "Textures" / "icon_notes.png"
    write_png(str(out), downsample())
    print("wrote", out, out.stat().st_size, "bytes")


if __name__ == "__main__":
    main()
