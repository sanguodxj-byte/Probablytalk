#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成顾客名册图标（记事本样式）：Textures/icon_notes.png

与 icon_wrench / icon_gear 同一套路：128x128、透明底、黑色描边、内部留白。
4 倍超采样后盒式降采样取抗锯齿；纯标准库写 PNG，不依赖 PIL。
图标是随构建部署的资产，画不出来就得改脚本重跑，不在编辑器里手点。
"""

import struct
import zlib
from pathlib import Path

SS = 4
SIZE = 128
BIG = SIZE * SS
STROKE = 7          # 最终图上的描边粗细（像素），与扳手/齿轮同量级
BLACK = (24, 24, 26)
WHITE = (246, 246, 244)

ALPHA = bytearray(BIG * BIG)
COLOR = bytearray(BIG * BIG * 3)


def blend(x, y, rgb, coverage):
    """alpha over：覆盖率不足一像素时按比例混色，边缘才有抗锯齿。"""
    if coverage <= 0:
        return
    p = y * BIG + x
    dst = ALPHA[p] / 255.0
    out = coverage + dst * (1 - coverage)
    ci = p * 3
    for c in range(3):
        src_v = rgb[c] / 255.0
        cur = COLOR[ci + c] / 255.0
        COLOR[ci + c] = int(((src_v * coverage + cur * dst * (1 - coverage)) / out * 255)) if out > 0 else 0
    ALPHA[p] = int(out * 255)


def round_rect(x0, y0, x1, y1, r, rgb, half_stroke=None):
    """圆角矩形：half_stroke 为 None 时整块填充，否则只画描边。"""
    pad = (half_stroke or 0) + r + 1
    for y in range(max(0, int(y0 - pad)), min(BIG, int(y1 + pad) + 1)):
        for x in range(max(0, int(x0 - pad)), min(BIG, int(x1 + pad) + 1)):
            cx = min(max(x, x0 + r), x1 - r)
            cy = min(max(y, y0 + r), y1 - r)
            dx = x - cx
            dy = y - cy
            d = ((max(dx, 0) ** 2 + max(dy, 0) ** 2) ** 0.5) - r
            if d < 0:
                d = min(max(dx, dy), 0.0) - r
            edge = abs(d)
            if half_stroke is not None and edge > half_stroke:
                continue
            blend(x, y, rgb, 1.0 if edge < 0.5 else max(0.0, 1.0 - (edge - 0.5)))


def circle_ring(ccx, ccy, rad, thick, rgb):
    half = thick / 2.0
    for y in range(max(0, int(ccy - rad - half)), min(BIG, int(ccy + rad + half) + 1)):
        for x in range(max(0, int(ccx - rad - half)), min(BIG, int(ccx + rad + half) + 1)):
            edge = abs(((x - ccx) ** 2 + (y - ccy) ** 2) ** 0.5 - rad)
            if edge > half:
                continue
            blend(x, y, rgb, 1.0 if edge < 0.5 else max(0.0, 1.0 - (edge - 0.5)))


def downsample():
    """盒式降采样。alpha 取多数票二值化：同族图标 (icon_gear/icon_wrench) 都是硬边无半透明，
    留灰边会在游戏里 Point 过滤下显脏。"""
    out = bytearray(SIZE * SIZE * 4)
    n = SS * SS
    for y in range(SIZE):
        for x in range(SIZE):
            sum_a = 0
            sums = [0, 0, 0]
            for sy in range(SS):
                for sx in range(SS):
                    p = (y * SS + sy) * BIG + x * SS + sx
                    a = ALPHA[p]
                    sum_a += a
                    for c in range(3):
                        sums[c] += COLOR[p * 3 + c] * a
            o = (y * SIZE + x) * 4
            if sum_a > 0:
                for c in range(3):
                    out[o + c] = min(255, sums[c] // sum_a)
            out[o + 3] = 255 if sum_a * 2 >= n * 255 else 0
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
    s = SS
    stroke = STROKE * s
    half = stroke / 2.0

    # 纸面：留白 + 黑描边
    page = (30 * s, 28 * s, 98 * s, 106 * s, 6 * s)
    round_rect(*page, rgb=WHITE)
    round_rect(*page, rgb=BLACK, half_stroke=half)

    # 线圈装订：骑在上边缘的 4 个环
    for i in range(4):
        circle_ring((44 + i * 15) * s, 28 * s, 7 * s, stroke, BLACK)

    # 三行字：最后一行短一点，读起来像写过东西的记事本
    for x0, y, x1 in [(44, 58, 84), (44, 74, 84), (44, 90, 72)]:
        round_rect(x0 * s, y * s - half, x1 * s, y * s + half, half, BLACK)

    out = Path(__file__).resolve().parents[1] / "Textures" / "icon_notes.png"
    write_png(str(out), downsample())
    print("wrote", out, out.stat().st_size, "bytes")


if __name__ == "__main__":
    main()
