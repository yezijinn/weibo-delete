#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把分镜渲染成 MP4。

    python video_render.py tts      生成配音和时间轴
    python video_render.py h        渲染横屏 1920x1080
    python video_render.py v        渲染竖屏 1080x1920
    python video_render.py all      全部
"""

import argparse
import asyncio
import json
import math
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "video"
TTS_DIR = OUT / "tts"
FPS = 30

BG      = (13, 17, 23)
BG_SOFT = (24, 30, 38)
ORANGE  = (255, 130, 0)
CYAN    = (0, 212, 255)
WHITE   = (240, 246, 252)
GREY    = (150, 160, 172)
DIM     = (68, 76, 86)
RED     = (248, 81, 73)
GREEN   = (63, 185, 80)

F_BOLD = "C:/Windows/Fonts/msyhbd.ttc"
F_REG  = "C:/Windows/Fonts/msyh.ttc"
F_MONO = "C:/Windows/Fonts/consola.ttf"

VOICE = "zh-CN-XiaoxiaoNeural"
LEAD = 0.45
TAIL = 0.80

SCENES = [
    dict(id="s01", main="weibo-delete", sub="",
         voice="这个开源小工具，叫 weibo delete。先看项目地址。", anim="intro"),

    dict(id="s02", main="点 Code → Download ZIP", sub="",
         voice="打开项目主页，点绿色的 Code 按钮，选 Download ZIP，下载完解压。",
         anim="shot", img="docs/screenshot-download.png"),

    dict(id="s03", main="双击 install.bat", sub="等一两分钟，自动装好",
         voice="进文件夹，双击 install 点 b a t，等一两分钟，环境自动装好。", anim="step"),

    dict(id="s04", main="双击 run.bat", sub="选菜单数字就能开删",
         voice="再双击 run 点 b a t，菜单里按数字选功能，就能开始删了。",
         anim="shot", img="docs/screenshot-menu.png"),

    dict(id="s05", main="手动删 10000 条", sub="要 3 天，手指点到抽筋",
         voice="以前手动删一万条，得删三天，手指点到抽筋。", anim="slam"),

    dict(id="s06", main="扫码登录一次", sub="剩下它自己删",
         voice="现在只要扫码登录一次，剩下它自己删。", anim="slide"),

    dict(id="s07", main="2~3 秒 / 条", sub="一万条 ≈ 挂一晚上",
         voice="平均两三秒一条，一万条挂一晚上，第二天就干净了。", anim="progress"),

    dict(id="s08", main="", sub="运行时实时打印进度",
         voice="跑起来是这样，黑窗口实时打印每一条的进度。",
         anim="shot", img="docs/screenshot-01.png"),

    dict(id="s09", main="", sub="每删一条立刻存盘",
         voice="每删一条立刻写进硬盘，哪怕突然断电也不丢进度。",
         anim="shot", img="docs/screenshot-02.png"),

    dict(id="s10", main="", sub="想停就停",
         voice="删到一半想停就停，按 control 加 c 中断就行。",
         anim="shot", img="docs/screenshot-03.png"),

    dict(id="s11", main="", sub="下次自动接着删",
         voice="下次运行会自动跳过已经删掉的，从断的地方接着删。",
         anim="shot", img="docs/screenshot-04.png"),

    dict(id="s12", main="日志里的吐槽", sub="",
         voice="跑的时候还会吐一些吐槽。这条，二零一三年的黑历史，替你感到解脱。这条转发抽奖没中，删了不亏。检测到五条深夜 emo 发言，已清理。这条零点赞，删了也没人发现。",
         anim="logs"),

    dict(id="s13", main="按日期删", sub="比如只删 2013 → 2016",
         voice="还能按日期删，比如只删二零一三到二零一六那几年。", anim="timeline"),

    dict(id="s14", main="密码不离开你的电脑", sub="全程本地 · 不经过任何服务器",
         voice="全程本地运行，账号密码不上传任何服务器。", anim="shield"),

    dict(id="s15", main="github.com/yezijinn/weibo-delete", sub="点个 star 就是最大的支持",
         voice="地址是 github 点 com 斜杠 ye zi jinn 斜杠 weibo delete。觉得有用，点个 star 就是最大的支持。", anim="outro"),
]

URL = "https://github.com/yezijinn/weibo-delete"


def run_ff(args):
    return subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y"] + args,
                          check=True)


def probe(path):
    r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration",
                        "-of", "default=nw=1:nk=1", str(path)],
                       capture_output=True, text=True, check=True)
    return float(r.stdout.strip())


_cache = {}
_M = 1080   # 高度基准 = min(W, H)
_W = 1920   # 宽度基准 = 当前画布宽


def fit_font(text, path, size, maxw):
    """从 size 往下试，返回能放进 maxw 的最大字号。"""
    d = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    size = int(size)
    while size > 16:
        f = font(path, size)
        if d.textlength(text, font=f) <= maxw:
            return f
        size -= 3
    return font(path, 16)


def font(path, size):
    size = max(8, int(size))
    k = (path, size)
    if k not in _cache:
        _cache[k] = ImageFont.truetype(path, size)
    return _cache[k]


def clamp(v, a=0.0, b=1.0):
    return max(a, min(b, v))


def lerp(a, b, t):
    return a + (b - a) * t


def ease_out(t):
    t = clamp(t)
    return 1 - (1 - t) ** 3


def ease_in_out(t):
    t = clamp(t)
    return t * t * (3 - 2 * t)


def ease_back(t):
    t = clamp(t)
    c = 1.70158
    return 1 + (c + 1) * (t - 1) ** 3 + c * (t - 1) ** 2


def entry(t, dur, in_t=0.55, out_t=0.35):
    k = ease_out(t / in_t) if in_t > 0 else 1.0
    a = 1.0
    if t < in_t:
        a = clamp(t / in_t * 1.3)
    out_start = dur - out_t
    if t > out_start and out_t > 0:
        a = min(a, 1.0 - ease_in_out((t - out_start) / out_t))
    return clamp(a), k


def tw(draw, s, f):
    return draw.textlength(s, font=f)


def _text_tile(txt, f, fill, alpha, pad=18):
    """把一段字渲染到刚好包住它的小 RGBA 图上。"""
    tmp = Image.new("RGBA", (1, 1))
    box = ImageDraw.Draw(tmp).textbbox((0, 0), txt, font=f)
    w = max(1, box[2] - box[0]) + pad * 2
    h = max(1, box[3] - box[1]) + pad * 2
    tile = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(tile).text((pad - box[0], pad - box[1]), txt, font=f,
                              fill=tuple(fill) + (int(255 * alpha),))
    return tile, box


def paste(img, txt, f, xy, fill, alpha=1.0, anchor="mm", shadow=True):
    alpha = clamp(alpha)
    if alpha <= 0.02 or not txt:
        return
    tile, box = _text_tile(txt, f, fill, alpha)
    tw_, th_ = tile.size
    cx, cy = xy
    if anchor == "mm":
        px, py = int(cx - tw_ / 2), int(cy - th_ / 2)
    elif anchor == "lm":
        px, py = int(cx), int(cy - th_ / 2)
    else:
        px, py = int(cx - tw_ / 2), int(cy - th_ / 2)

    if shadow:
        sh = Image.new("RGBA", (tw_, th_), (0, 0, 0, 0))
        ImageDraw.Draw(sh).text((18 + 3 - box[0], 18 + 5 - box[1]), txt, font=f,
                                fill=(0, 0, 0, int(150 * alpha)))
        sh = sh.filter(ImageFilter.GaussianBlur(7))
        img.alpha_composite(sh, (px, py))
    img.alpha_composite(tile, (px, py))


def rrect(img, box, radius, fill=None, outline=None, width=2, alpha=255):
    x0, y0, x1, y1 = [int(v) for v in box]
    pad = width + 2
    w, h = max(1, x1 - x0 + pad * 2), max(1, y1 - y0 + pad * 2)
    tile = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    fl = tuple(fill) + (int(alpha),) if fill else None
    ol = tuple(outline) + (int(alpha),) if outline else None
    ImageDraw.Draw(tile).rounded_rectangle([pad, pad, pad + (x1 - x0), pad + (y1 - y0)],
                                           radius=radius, fill=fl, outline=ol, width=width)
    img.alpha_composite(tile, (x0 - pad, y0 - pad))


def make_bg(w, h):
    img = Image.new("RGBA", (w, h), BG)
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / max(1, h - 1)
        c = tuple(int(lerp(BG[i], BG_SOFT[i], t * 0.9)) for i in range(3))
        d.line([(0, y), (w, y)], fill=c + (255,))
    grid = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grid)
    step = max(52, int(min(w, h) * 0.058))
    for x in range(0, w, step):
        gd.line([(x, 0), (x, h)], fill=(255, 255, 255, 8))
    for y in range(0, h, step):
        gd.line([(0, y), (w, y)], fill=(255, 255, 255, 8))
    img.alpha_composite(grid)
    g1 = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    r1 = int(min(w, h) * 0.75)
    ImageDraw.Draw(g1).ellipse([-r1 // 2, -r1 // 2, r1, r1], fill=ORANGE + (48,))
    img.alpha_composite(g1.filter(ImageFilter.GaussianBlur(int(min(w, h) * 0.10))))
    g2 = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    r2 = int(min(w, h) * 0.62)
    ImageDraw.Draw(g2).ellipse([w - r2, h - r2, w + r2 // 2, h + r2 // 2],
                               fill=CYAN + (32,))
    img.alpha_composite(g2.filter(ImageFilter.GaussianBlur(int(min(w, h) * 0.10))))
    return img


def star_pts(cx, cy, r):
    pts = []
    for i in range(10):
        ang = -math.pi / 2 + i * math.pi / 5
        rr = r if i % 2 == 0 else r * 0.42
        pts.append((cx + rr * math.cos(ang), cy + rr * math.sin(ang)))
    return pts


_img_cache = {}


def load_shot(rel, maxw, maxh):
    """载入截图，等比缩放到不超过 maxw x maxh。"""
    k = (rel, maxw, maxh)
    if k in _img_cache:
        return _img_cache[k]
    p = ROOT / rel
    if not p.exists():
        return None
    im = Image.open(p).convert("RGB")
    w, h = im.size
    sc = min(maxw / w, maxh / h)
    nw, nh = max(1, int(w * sc)), max(1, int(h * sc))
    im = im.resize((nw, nh), Image.LANCZOS)
    _img_cache[k] = im
    return im


def paste_shot(img, shot, cx, cy, a, outline=ORANGE):
    """把截图贴到画布中央，带圆角和边框。"""
    if shot is None:
        return
    w, h = shot.size
    pad = 8
    x0, y0 = int(cx - w / 2), int(cy - h / 2)
    # 阴影
    sh = Image.new("RGBA", (w + pad * 2, h + pad * 2), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([pad, pad, pad + w, pad + h], 12,
                                         fill=(0, 0, 0, int(180 * a)))
    sh = sh.filter(ImageFilter.GaussianBlur(16))
    img.alpha_composite(sh, (x0 - pad, y0 - pad + 10))
    # 图
    tile = shot.convert("RGBA")
    if a < 0.99:
        al = tile.getchannel("A").point(lambda v: int(v * a))
        tile.putalpha(al)
    img.alpha_composite(tile, (x0, y0))
    # 边框
    rrect(img, [x0 - 2, y0 - 2, x0 + w + 2, y0 + h + 2], 12,
          outline=outline, width=3, alpha=int(220 * a))


def sc_count(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur)
    n = int(lerp(0, 10000, ease_out(t / min(3.0, dur))))
    paste(img, format(n, ","), font(F_BOLD, int(_M * 0.20)), (cx, cy - _M * 0.03),
          ORANGE, a)
    paste(img, "条微博", font(F_REG, int(_M * 0.055)), (cx, cy + _M * 0.11),
          WHITE, a * 0.95)
    if t > 1.2:
        a2 = clamp((t - 1.2) / 0.6)
        paste(img, S["sub"], font(F_REG, int(_M * 0.062)), (cx, cy + _M * 0.24), GREY, a2)


def sc_slam(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.30, out_t=0.35)
    sc = lerp(1.7, 1.0, ease_back(t / 0.30))
    jitter = 0
    if t < 0.45:
        jitter = int(lerp(12, 0, t / 0.45)) * (1 if int(t * 60) % 2 else -1)
    f = fit_font(S["main"], F_BOLD, _M * 0.145 * sc, _W * 0.88)
    paste(img, S["main"], f, (cx + jitter, cy - _M * 0.05), RED, a)
    if t > 0.5:
        a2 = clamp((t - 0.5) / 0.5)
        f2 = fit_font(S["sub"], F_REG, _M * 0.055, _W * 0.88)
        paste(img, S["sub"], f2, (cx, cy + _M * 0.08), WHITE, a2)
    if t > 0.25:
        w = int(W * 0.22 * ease_out((t - 0.25) / 0.5))
        if w > 0:
            rrect(img, [cx - w // 2, cy + _M * 0.15, cx + w // 2, cy + _M * 0.15 + 6],
                  3, fill=RED, alpha=int(200 * a))


def sc_reveal(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    d = ImageDraw.Draw(img)
    a, k = entry(t, dur, in_t=0.5, out_t=0.35)
    f = fit_font(S["main"], F_BOLD, _M * 0.098, _W * 0.88)
    txt = S["main"]
    n = int(len(txt) * ease_out(t / min(1.1, dur)))
    paste(img, txt[:max(1, n)], f, (cx, cy - _M * 0.06), WHITE, a)
    if t > 1.1:
        a2 = clamp((t - 1.1) / 0.6)
        f2 = font(F_REG, int(_M * 0.058))
        paste(img, S["sub"], f2, (cx, cy + _M * 0.10), GREY, a2)
        w = tw(d, S["sub"], f2)
        if a2 > 0.3:
            rrect(img, [cx - w / 2, cy + _M * 0.10, cx + w / 2, cy + _M * 0.10 + 4],
                  2, fill=RED, alpha=int(220 * a2))


def sc_sweep(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.7, out_t=0.35)
    sc = lerp(0.85, 1.0, ease_out(t / 0.7))
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.135 * sc, _W * 0.88), (cx, cy), WHITE, a)
    if 0.1 < t < 1.3:
        p = ease_in_out((t - 0.1) / 1.2)
        x = lerp(-W * 0.1, W * 1.1, p)
        bw = int(W * 0.10)
        pad = 40
        tw_ = bw + pad * 2
        tile = Image.new("RGBA", (tw_, H), (0, 0, 0, 0))
        td = ImageDraw.Draw(tile)
        for i in range(bw):
            aa = int(120 * (1 - i / bw))
            td.line([(pad + i, 0), (pad + i, H)], fill=ORANGE + (aa,))
        tile = tile.filter(ImageFilter.GaussianBlur(10))
        img.alpha_composite(tile, (int(x) - pad, 0))


def sc_slide(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.65, out_t=0.35)
    x1 = lerp(-W * 0.35, 0, ease_out(t / 0.65))
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.135, _W * 0.88), (cx + x1, cy - _M * 0.07), WHITE, a)
    if t > 0.55:
        a2 = clamp((t - 0.55) / 0.6)
        x2 = lerp(W * 0.35, 0, ease_out((t - 0.55) / 0.6))
        paste(img, S["sub"], font(F_BOLD, int(_M * 0.095)), (cx + x2, cy + _M * 0.09),
              ORANGE, a2)


def sc_progress(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.6, out_t=0.35)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.135, _W * 0.88), (cx, cy - _M * 0.13), ORANGE, a)
    if t > 0.6:
        a2 = clamp((t - 0.6) / 0.5)
        paste(img, S["sub"], font(F_REG, int(_M * 0.062)), (cx, cy + _M * 0.01), WHITE, a2)
    if t > 0.9:
        a3 = clamp((t - 0.9) / 0.4)
        bw = int(W * 0.62)
        bx0, by = cx - bw // 2, cy + _M * 0.14
        rrect(img, [bx0, by, bx0 + bw, by + 12], 6, fill=DIM, alpha=int(160 * a3))
        p = ease_in_out(clamp((t - 0.9) / max(0.5, min(3.5, dur - 0.9))))
        fw = int(bw * p)
        if fw > 0:
            rrect(img, [bx0, by, bx0 + fw, by + 12], 6, fill=ORANGE, alpha=int(255 * a3))


def sc_break(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.6, out_t=0.35)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.125, _W * 0.88), (cx, cy - _M * 0.12), WHITE, a)
    if t > 0.6:
        a2 = clamp((t - 0.6) / 0.5)
        paste(img, S["sub"], font(F_REG, int(_M * 0.058)), (cx, cy + _M * 0.02), GREY, a2)
    if t > 0.9:
        bw = int(W * 0.56)
        bx0, by = cx - bw // 2, cy + _M * 0.13
        rrect(img, [bx0, by, bx0 + bw, by + 10], 5, fill=DIM, alpha=190)
        pa = clamp((t - 0.9) / 0.9)
        fw = int(bw * 0.52)
        if fw > 0:
            rrect(img, [bx0, by, bx0 + fw, by + 10], 5, fill=ORANGE, alpha=255)
        gx = bx0 + fw
        pulse = 0.6 + 0.4 * math.sin(t * 7)
        rr = int(_M * 0.018 * (1 + 0.25 * pulse))
        rrect(img, [gx - rr, by - rr + 5, gx + rr, by + rr + 5], rr,
              fill=RED, alpha=int(230 * pa))
        if t > 1.8:
            pa2 = clamp((t - 1.8) / 0.8)
            fw2 = int(bw * lerp(0.52, 1.0, ease_in_out((t - 1.8) / 1.6)))
            rrect(img, [gx, by, bx0 + fw2, by + 10], 5, fill=ORANGE, alpha=int(255 * pa2))


def sc_ring(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2 - _M * 0.04
    a, k = entry(t, dur, in_t=0.6, out_t=0.35)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.125, _W * 0.88), (cx, cy - _M * 0.18), WHITE, a)
    if t > 0.7:
        a2 = clamp((t - 0.7) / 0.5)
        paste(img, S["sub"], font(F_REG, int(_M * 0.052)), (cx, cy + _M * 0.22), GREY, a2)
    if t > 0.8:
        a3 = clamp((t - 0.8) / 0.4)
        R = int(_M * 0.115)
        lay = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ld = ImageDraw.Draw(lay)
        ld.ellipse([cx - R, cy - R, cx + R, cy + R], outline=DIM + (int(170 * a3),), width=9)
        span = int(360 * ease_in_out(clamp((t - 0.8) / max(0.5, min(3.6, dur - 0.8)))))
        if span > 0:
            ld.arc([cx - R, cy - R, cx + R, cy + R], -90, -90 + span,
                   fill=ORANGE + (int(255 * a3),), width=9)
        img.alpha_composite(lay)
        nums = ["30", "60", "120"]
        idx = min(2, int(clamp((t - 0.8) / max(0.1, dur - 0.8)) * 3))
        paste(img, nums[idx] + "s", font(F_BOLD, int(_M * 0.072)), (cx, cy), ORANGE, a3)


def sc_shield(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2 - _M * 0.05
    a, k = entry(t, dur, in_t=0.7, out_t=0.35)
    if t > 0.2:
        a3 = clamp((t - 0.2) / 0.6)
        R = int(_M * 0.115)
        top, bot = cy - R, cy + R
        pts = [(cx - R, top + R * 0.35), (cx, top - R * 0.12), (cx + R, top + R * 0.35),
               (cx + R * 0.86, bot - R * 0.28), (cx, bot + R * 0.18),
               (cx - R * 0.86, bot - R * 0.28)]
        lay = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ImageDraw.Draw(lay).polygon(pts, outline=GREEN + (int(255 * a3),), width=8)
        img.alpha_composite(lay)
        if t > 0.9:
            a4 = clamp((t - 0.9) / 0.5)
            lay2 = Image.new("RGBA", img.size, (0, 0, 0, 0))
            ImageDraw.Draw(lay2).line([(cx - R * 0.42, cy + R * 0.02),
                                       (cx - R * 0.08, cy + R * 0.36),
                                       (cx + R * 0.48, cy - R * 0.34)],
                                      fill=GREEN + (int(255 * a4),), width=12, joint="curve")
            img.alpha_composite(lay2)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.098, _W * 0.88), (cx, cy + _M * 0.24), WHITE, a)
    if t > 1.0:
        a2 = clamp((t - 1.0) / 0.5)
        paste(img, S["sub"], font(F_REG, int(_M * 0.048)), (cx, cy + _M * 0.33), GREY, a2)


def sc_star(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2 - _M * 0.04
    a, k = entry(t, dur, in_t=0.6, out_t=0.35)
    R = int(_M * 0.13)
    sc = lerp(0.2, 1.0, ease_back(t / 1.0)) if t < 1.0 else 1.0
    pts = star_pts(cx, cy, R * sc)
    lay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(lay).polygon(pts, fill=ORANGE + (int(255 * a),))
    img.alpha_composite(lay)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.105, _W * 0.88), (cx, cy + _M * 0.22), WHITE, a)


def sc_cards(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.5, out_t=0.35)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.10, _W * 0.88), (cx, cy - _M * 0.24), WHITE, a)
    if t > 0.5:
        a2 = clamp((t - 0.5) / 0.5)
        paste(img, S["sub"], font(F_REG, int(_M * 0.045)), (cx, cy - _M * 0.145), GREY, a2)
    labels = ["下载", "双击", "开删"]
    if t > 0.75:
        cw, ch = int(W * 0.20), int(_M * 0.20)
        gap = int(W * 0.035)
        total = cw * 3 + gap * 2
        x0 = cx - total // 2
        for i, lab in enumerate(labels):
            p = clamp((t - 0.75 - i * 0.22) / 0.5)
            if p <= 0:
                continue
            yoff = int(lerp(_M * 0.06, 0, ease_back(p)))
            al = clamp(p * 1.4)
            x = x0 + i * (cw + gap)
            y = cy - ch // 2 + _M * 0.06 + yoff
            rrect(img, [x, y, x + cw, y + ch], 18, fill=(30, 38, 48),
                  outline=ORANGE if i == 2 else DIM, width=3, alpha=int(255 * al))
            paste(img, lab, font(F_BOLD, int(_M * 0.062)),
                  (x + cw // 2, y + ch // 2), ORANGE if i == 2 else WHITE, al)
            if i < 2:
                aa = clamp((t - 0.95 - i * 0.22) / 0.4)
                if aa > 0:
                    paste(img, ">", font(F_BOLD, int(_M * 0.07)),
                          (x + cw + gap // 2, y + ch // 2), GREY, aa)


def sc_typing(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.5, out_t=0.35)
    f = font(F_MONO, int(_M * 0.048))
    txt = S["main"]
    n = int(len(txt) * ease_in_out(t / min(1.6, dur)))
    shown = txt[:max(1, n)]
    caret = "|" if int(t * 2) % 2 == 0 and n < len(txt) else ""
    paste(img, shown + caret, f, (cx, cy - _M * 0.06), CYAN, a)
    if t > 1.7:
        a2 = clamp((t - 1.7) / 0.6)
        qs = int(_M * 0.20)
        try:
            q = qr_image(URL, qs).convert("RGBA")
            lay = Image.new("RGBA", img.size, (0, 0, 0, 0))
            lay.paste(q, (int(cx - qs / 2), int(cy + _M * 0.03)), q)
            al = lay.getchannel("A").point(lambda v: int(v * a2))
            lay.putalpha(al)
            img.alpha_composite(lay)
            rrect(img, [cx - qs / 2 - 4, cy + _M * 0.03 - 4,
                        cx + qs / 2 + 4, cy + _M * 0.03 + qs + 4], 10,
                  outline=ORANGE, width=3, alpha=int(255 * a2))
        except Exception as e:
            sys.stderr.write("qr failed: %s\n" % e)
        paste(img, S["sub"], font(F_REG, int(_M * 0.042)),
              (cx, cy + _M * 0.03 + qs + _M * 0.055), GREY, a2)


# (编号, 微博 id, 正文) —— 正文尽量选能勾人回复的
LOG_LINES = [
    ("276", "37131233313", "不要说我变了，我只不过学会了，别人怎样对我，我就该怎样去对别人。"),
    ("277", "37131233312", "人生苦短，能和家人相处的时间，其实是有限的。"),
    ("278", "37131233311", "如果你改变不了世界，也不要让世界改变纯真的你。"),
    ("279", "37131233310", "当时觉得很有道理，现在只想找个地缝钻进去。"),
    ("280", "37131233309", "这条当年 0 赞，现在还是 0 赞，删了不亏。"),
    ("281", "37131233308", "凌晨三点发的深夜 emo，天亮就后悔了，帮你清掉。"),
    ("282", "37131233307", "发誓减肥的第 37 条，一次都没成功，删了重新开始。"),
    ("283", "37131233306", "转发抽奖没中，还占地方，删。"),
    ("284", "37131233305", "配图是当年的非主流头像，为了你好，删。"),
    ("285", "37131233304", "追星发的，如今爱豆塌房了，删得心安理得。"),
]


def sc_intro(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.5, out_t=0.35)
    sc = lerp(0.9, 1.0, ease_out(t / 0.6))
    f = fit_font("weibo-delete", F_BOLD, _M * 0.145 * sc, _W * 0.88)
    paste(img, "weibo-delete", f, (cx, cy - _M * 0.10), WHITE, a)
    if t > 0.5:
        a2 = clamp((t - 0.5) / 0.5)
        paste(img, "开源 · 免费 · 一行命令开删", font(F_REG, int(_M * 0.048)),
              (cx, cy + _M * 0.03), GREY, a2)
    if t > 1.1:
        a3 = clamp((t - 1.1) / 0.5)
        url = "github.com/yezijinn/weibo-delete"
        n = int(len(url) * ease_in_out((t - 1.1) / min(1.6, dur - 1.1)))
        paste(img, url[:max(1, n)], font(F_MONO, int(_M * 0.050)),
              (cx, cy + _M * 0.16), CYAN, a3)
        w = ImageDraw.Draw(img).textlength(url, font=font(F_MONO, int(_M * 0.050)))
        if a3 > 0.5:
            rrect(img, [cx - w / 2 - 14, cy + _M * 0.16 - _M * 0.045,
                        cx + w / 2 + 14, cy + _M * 0.16 + _M * 0.045], 10,
                  fill=(20, 30, 40), alpha=int(180 * a3))
            paste(img, url[:max(1, n)], font(F_MONO, int(_M * 0.050)),
                  (cx, cy + _M * 0.16), CYAN, a3)


def sc_shot(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.5, out_t=0.35)
    shot = load_shot(S["img"], int(W * 0.80), int(H * 0.62))
    if shot is None:
        paste(img, "(截图缺失)", font(F_REG, int(_M * 0.06)), (cx, cy), RED, a)
        return
    if S.get("sub"):
        paste(img, S["sub"], font(F_BOLD, int(_M * 0.062)),
              (cx, cy - _M * 0.36), WHITE, a * clamp(t / 0.4))
    kk = ease_out(t / 0.5)
    yoff = lerp(_M * 0.05, 0, kk)
    sc = lerp(0.94, 1.0, kk)
    if sc < 0.999:
        sw, sh_ = shot.size
        shot = shot.resize((int(sw * sc), int(sh_ * sc)), Image.LANCZOS)
    paste_shot(img, shot, cx, cy + _M * 0.06 + yoff, a)


def sc_step(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.5, out_t=0.35)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.115, _W * 0.88),
          (cx, cy - _M * 0.07), ORANGE, a)
    if t > 0.5:
        a2 = clamp((t - 0.5) / 0.5)
        paste(img, S["sub"], font(F_REG, int(_M * 0.055)), (cx, cy + _M * 0.06),
              WHITE, a2)
    if t > 0.9:
        a3 = clamp((t - 0.9) / 0.4)
        bw = int(W * 0.40)
        bx0, by = cx - bw // 2, cy + _M * 0.14
        rrect(img, [bx0, by, bx0 + bw, by + 10], 5, fill=DIM, alpha=int(160 * a3))
        p = ease_in_out(clamp((t - 0.9) / max(0.5, min(2.2, dur - 0.9))))
        fw = int(bw * p)
        if fw > 0:
            rrect(img, [bx0, by, bx0 + fw, by + 10], 5, fill=ORANGE, alpha=int(255 * a3))


def sc_logs(img, t, dur, W, H, S):
    """仿真实运行日志，每条两行：元信息一行 + 正文一行。"""
    x0 = W * 0.055
    a, k = entry(t, dur, in_t=0.4, out_t=0.4)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.062, _W * 0.88), (W / 2, _M * 0.085),
          ORANGE, a)
    paste(img, "真实运行日志 · 每分钟一条", font(F_REG, int(_M * 0.036)),
          (W / 2, _M * 0.145), GREY, a * 0.9)

    body_maxw = _W * 0.86 - _M * 0.035
    f_meta = font(F_MONO, int(_M * 0.026))

    top = _M * 0.215
    block = _M * 0.115          # 每条占的高度
    visible = 3

    total = len(LOG_LINES)
    shown = clamp(t / 1.5, 0, total)
    start = max(0, int(shown) - visible + 1)

    for i in range(start, min(total, start + visible)):
        p = clamp(shown - i)
        if p <= 0:
            continue
        al = clamp(p * 2.2) * a
        y = top + (i - start) * block
        num, mid, body = LOG_LINES[i]
        meta = "[2026-09-14 14:%02d:%02d] [INFO] [%s] 已删除 %s" % (
            4 + i // 3, (i * 13) % 60, num, mid)
        paste(img, meta, f_meta, (x0, y), GREY, al, anchor="lm", shadow=False)
        fb = fit_font(body, F_REG, _M * 0.034, body_maxw)
        paste(img, body, fb, (x0 + _M * 0.035, y + _M * 0.050),
              WHITE, al, anchor="lm", shadow=False)
        # 左侧橙色竖线
        if al > 0.3:
            rrect(img, [x0 - _M * 0.020, y - _M * 0.022,
                        x0 - _M * 0.016, y + _M * 0.072],
                  2, fill=ORANGE, alpha=int(230 * al))

    # 光标
    fcur = font(F_MONO, int(_M * 0.030))
    cy_ = top + visible * block - _M * 0.01
    if int(t * 3) % 2 == 0:
        paste(img, "_", fcur, (x0, cy_), ORANGE, a, anchor="lm", shadow=False)


def sc_timeline(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.6, out_t=0.35)
    paste(img, S["main"], fit_font(S["main"], F_BOLD, _M * 0.115, _W * 0.88), (cx, cy - _M * 0.20), WHITE, a)
    if t > 0.6:
        a2 = clamp((t - 0.6) / 0.5)
        paste(img, S["sub"], font(F_BOLD, int(_M * 0.070)), (cx, cy + _M * 0.13), ORANGE, a2)
    if t > 0.7:
        a3 = clamp((t - 0.7) / 0.4)
        bw = int(W * 0.70)
        bx0, by = cx - bw // 2, cy - _M * 0.005
        rrect(img, [bx0, by, bx0 + bw, by + 8], 4, fill=DIM, alpha=int(180 * a3))
        p = ease_in_out(clamp((t - 0.9) / 1.4))
        fw = int(bw * 0.58 * p)
        if fw > 0:
            rrect(img, [bx0, by, bx0 + fw, by + 8], 4, fill=ORANGE, alpha=int(255 * a3))


def sc_outro(img, t, dur, W, H, S):
    cx, cy = W / 2, H / 2
    a, k = entry(t, dur, in_t=0.5, out_t=0.35)
    f = font(F_MONO, int(_M * 0.052))
    url = "github.com/yezijinn/weibo-delete"
    n = int(len(url) * ease_in_out(t / min(1.5, dur)))
    paste(img, url[:max(1, n)] + ("|" if int(t * 2) % 2 == 0 and n < len(url) else ""),
          f, (cx, cy - _M * 0.10), CYAN, a)
    if t > 1.4:
        a2 = clamp((t - 1.4) / 0.5)
        paste(img, S["sub"], font(F_BOLD, int(_M * 0.062)),
              (cx, cy + _M * 0.05), ORANGE, a2)
        # 星星
        R = int(_M * 0.05)
        for i, off in enumerate([-1, 0, 1]):
            pp = clamp((t - 1.6 - i * 0.15) / 0.4)
            if pp <= 0:
                continue
            pts = star_pts(cx + off * _M * 0.075, cy + _M * 0.20, R * ease_back(pp))
            lay = Image.new("RGBA", img.size, (0, 0, 0, 0))
            ImageDraw.Draw(lay).polygon(pts, fill=ORANGE + (int(255 * a2 * pp),))
            img.alpha_composite(lay)
    if t > 2.2:
        a3 = clamp((t - 2.2) / 0.5)
        paste(img, "weibo-delete", font(F_BOLD, int(_M * 0.048)),
              (cx, cy + _M * 0.32), GREY, a3)


DISPATCH = {
    "count": sc_count, "slam": sc_slam, "reveal": sc_reveal, "sweep": sc_sweep,
    "slide": sc_slide, "progress": sc_progress, "timeline": sc_timeline,
    "break": sc_break, "ring": sc_ring, "shield": sc_shield, "star": sc_star,
    "cards": sc_cards, "typing": sc_typing,
    "intro": sc_intro, "shot": sc_shot, "step": sc_step, "logs": sc_logs,
    "outro": sc_outro,
}


_bg_cache = {}


def get_bg(W, H):
    k = (W, H)
    if k not in _bg_cache:
        _bg_cache[k] = make_bg(W, H)
    return _bg_cache[k]


def render_scene(idx, S, dur, W, H, outdir):
    sc = DISPATCH[S["anim"]]
    total = int(dur * FPS)
    bg = get_bg(W, H)
    for fi in range(total):
        t = fi / FPS
        img = bg.copy()
        sc(img, t, dur, W, H, S)
        img.convert("RGB").save(outdir / ("%03d.png" % fi), compress_level=1)


def render_video(W, H, label):
    timeline = json.loads((OUT / "timeline.json").read_text(encoding="utf-8"))

    silent = OUT / ("silent_" + label + ".mp4")

    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
           "-f", "rawvideo", "-pix_fmt", "rgb24",
           "-s", "%dx%d" % (W, H), "-r", str(FPS), "-i", "-",
           "-c:v", "libx264", "-preset", "medium", "-crf", "20",
           "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(silent)]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)

    global _M, _W
    _M = min(W, H)
    _W = W
    bg = get_bg(W, H)
    total_frames = int(sum(d for _, d in timeline) * FPS)
    written = 0
    for S, dur in timeline:
        sc = DISPATCH[S["anim"]]
        n = int(dur * FPS)
        for fi in range(n):
            t = fi / FPS
            img = bg.copy()
            sc(img, t, dur, W, H, S)
            proc.stdin.write(img.convert("RGB").tobytes())
            written += 1
        sys.stderr.write("  %s done (%d/%d)\n" % (S["id"], written, total_frames))
        sys.stderr.flush()

    proc.stdin.close()
    proc.wait()
    if proc.returncode != 0:
        raise RuntimeError("ffmpeg failed: %d" % proc.returncode)

    audio = OUT / "voice.mp3"
    final = OUT / ("weibo_delete_" + label + ".mp4")
    if audio.exists():
        run_ff(["-i", str(silent), "-i", str(audio),
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                "-shortest", "-movflags", "+faststart", str(final)])
    else:
        silent.replace(final)
    return final


async def _tts_one(text, path):
    import edge_tts
    last = None
    for attempt in range(5):
        try:
            c = edge_tts.Communicate(text, VOICE, rate="+8%")
            await c.save(str(path))
            return
        except Exception as e:
            last = e
            await asyncio.sleep(2 + attempt * 3)
    raise last


def do_tts():
    TTS_DIR.mkdir(parents=True, exist_ok=True)
    parts = []
    for S in SCENES:
        p = TTS_DIR / (S["id"] + ".mp3")
        ok = False
        if p.exists():
            try:
                probe(p)
                ok = True
            except Exception:
                p.unlink(missing_ok=True)
        for attempt in range(4):
            if ok:
                break
            asyncio.run(_tts_one(S["voice"], p))
            try:
                probe(p)
                ok = True
            except Exception:
                p.unlink(missing_ok=True)
                time.sleep(2)
        if not ok:
            raise RuntimeError("tts failed: " + S["id"])
        parts.append(p)
        print("tts", S["id"], "%.2fs" % probe(p))

    # 每段前面补 LEAD，后面补 TAIL
    clips = []
    for i, S in enumerate(SCENES):
        src = parts[i]
        d = probe(src)
        out = TTS_DIR / (S["id"] + "_pad.mp3")
        ms = int(LEAD * 1000)
        af = "adelay=%d|%d,apad=pad_dur=%.2f" % (ms, ms, TAIL)
        run_ff(["-i", str(src), "-af", af, str(out)])
        clips.append((out, LEAD + d + TAIL))

    # 串成整条
    lst = OUT / "voice_list.txt"
    with lst.open("w", encoding="utf-8") as f:
        for c, _ in clips:
            f.write("file '" + c.as_posix() + "'\n")
    run_ff(["-f", "concat", "-safe", "0", "-i", str(lst),
            "-c:a", "libmp3lame", "-b:a", "192k", str(OUT / "voice.mp3")])

    timeline = []
    for S, (_, d) in zip(SCENES, clips):
        timeline.append([S, round(d, 3)])
    (OUT / "timeline.json").write_text(
        json.dumps(timeline, ensure_ascii=False, indent=2), encoding="utf-8")
    total = sum(d for _, d in timeline)
    print("timeline ok, total %.2fs" % total)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["tts", "h", "v", "all"])
    a = ap.parse_args()
    OUT.mkdir(exist_ok=True)
    if a.cmd == "tts":
        do_tts()
    elif a.cmd == "h":
        print(render_video(1920, 1080, "1080p"))
    elif a.cmd == "v":
        print(render_video(1080, 1920, "vertical"))
    else:
        do_tts()
        print(render_video(1920, 1080, "1080p"))
        print(render_video(1080, 1920, "vertical"))


if __name__ == "__main__":
    main()
