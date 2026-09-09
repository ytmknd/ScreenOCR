import argparse
import json

import cv2
import numpy as np
from rapidocr import EngineType, LangDet, LangRec, ModelType, OCRVersion, RapidOCR

# 検出（DB）は文字の輪郭を閉じられないと領域を出せないため、切り抜きの縁に文字が接して
# いると 1 単語だけをぴったり選んだような範囲を取りこぼす。文字が小さいほど失敗しやすい。
# そこで検出前に (1) 高さが足りなければ拡大し (2) 背景色で余白を足す。
MIN_TEXT_HEIGHT = 48  # 拡大後に確保する高さ（px）
MAX_UPSCALE = 8.0
MAX_SIDE = 1600  # 拡大後の長辺の上限（px）
MARGIN_RATIO = 0.4  # 余白 = 短辺 * この比率
MIN_MARGIN = 16
MAX_MARGIN = 64
# 検出が空だったときの一段落とし（認識モデルだけを 1 行として実行する）の採用下限。
# RapidOCR の Global.text_score と同じ値。
FALLBACK_MIN_SCORE = 0.5
# 余白を足すと、稀に同じ行が「日」と「日本語」のように二重に検出される。縦の重なりが
# 小さい方の高さのこの割合以上なら「同じ行」とみなす。
SAME_ROW_RATIO = 0.7


def load_image(path):
    """BGR 3 チャンネルとして読み込む。`--ocr-file` には透過 PNG も渡され得るので、
    RapidOCR の LoadImage と同じく、文字の明るさに応じた背景色で不透明化する
    （画面キャプチャは常に不透明なので、その場合は素通し）。"""
    data = np.fromfile(path, dtype=np.uint8)
    image = cv2.imdecode(data, cv2.IMREAD_UNCHANGED)
    if image is None:
        raise ValueError(f"画像を読み込めませんでした: {path}")
    if image.ndim == 2:
        return cv2.cvtColor(image, cv2.COLOR_GRAY2BGR)
    if image.shape[2] == 2:
        gray = cv2.cvtColor(image[:, :, 0], cv2.COLOR_GRAY2BGR)
        image = np.dstack([gray, image[:, :, 1]])
    if image.shape[2] == 3:
        return image

    color = image[:, :, :3].astype(np.float32)
    alpha = (image[:, :, 3].astype(np.float32) / 255.0)[..., None]
    visible = image[:, :, :3][image[:, :, 3] > 0]
    background = 255.0
    if visible.size:
        luminance = 0.114 * visible[:, 0] + 0.587 * visible[:, 1] + 0.299 * visible[:, 2]
        background = 255.0 if float(np.mean(luminance)) < 128 else 0.0
    return (color * alpha + background * (1.0 - alpha)).astype(np.uint8)


def upscale(image):
    height, width = image.shape[:2]
    if height <= 0 or width <= 0 or height >= MIN_TEXT_HEIGHT:
        return image, 1.0
    scale = min(MIN_TEXT_HEIGHT / height, MAX_UPSCALE, MAX_SIDE / max(height, width))
    if scale <= 1.0:
        return image, 1.0
    resized = cv2.resize(
        image,
        (max(1, round(width * scale)), max(1, round(height * scale))),
        interpolation=cv2.INTER_CUBIC,
    )
    return resized, scale


def add_margin(image):
    """縁の画素から推定した背景色で余白を足す。BORDER_REPLICATE は縁に接した文字を
    引き伸ばして偽の線を作るため使わない。"""
    height, width = image.shape[:2]
    edges = np.concatenate([image[0, :], image[-1, :], image[:, 0], image[:, -1]])
    color = [float(x) for x in np.median(edges, axis=0)]
    margin = int(min(MAX_MARGIN, max(MIN_MARGIN, round(min(height, width) * MARGIN_RATIO))))
    bordered = cv2.copyMakeBorder(
        image, margin, margin, margin, margin, cv2.BORDER_CONSTANT, value=color
    )
    return bordered, margin


def to_original(box, scale, margin, width, height):
    """検出座標（拡大・余白つき）を元の切り抜きの座標へ戻す。"""
    points = []
    for point in np.asarray(box, dtype=float).reshape(-1, 2):
        x = (float(point[0]) - margin) / scale
        y = (float(point[1]) - margin) / scale
        points.append([min(max(x, 0.0), float(width)), min(max(y, 0.0), float(height))])
    return points


def bounds(points):
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


def area(rect):
    return max(0.0, rect[2] - rect[0]) * max(0.0, rect[3] - rect[1])


def is_duplicate_of(a, b):
    """a が b の二重検出か。同じ行（縦がほぼ重なる）で横も重なり、文字列が b に含まれるとき。
    同じ行に離れて現れる同じ語（「日本語 と 日」の「日」など）は横が重ならないので残る。"""
    if min(a[2], b[2]) - max(a[0], b[0]) <= 0:
        return False
    vertical = min(a[3], b[3]) - max(a[1], b[1])
    height = min(a[3] - a[1], b[3] - b[1])
    return height > 0 and vertical >= SAME_ROW_RATIO * height


def drop_redundant(lines):
    """同じ行を二重に検出した短い方を捨てる。"""
    rects = [bounds(line["boundingBox"]) for line in lines]
    kept = []
    for i, line in enumerate(lines):
        redundant = False
        for j, other in enumerate(lines):
            if i == j or line["text"] not in other["text"] or area(rects[i]) <= 0:
                continue
            smaller = area(rects[i]) < area(rects[j]) or (area(rects[i]) == area(rects[j]) and j < i)
            if smaller and is_duplicate_of(rects[i], rects[j]):
                redundant = True
                break
        if not redundant:
            kept.append(line)
    return kept


def build_lines(result, scale, margin, width, height):
    boxes = result.boxes if getattr(result, "boxes", None) is not None else []
    texts = result.txts if getattr(result, "txts", None) is not None else []
    scores = result.scores if getattr(result, "scores", None) is not None else []
    lines = []
    for box, text, score in zip(boxes, texts, scores):
        if not str(text).strip():
            continue
        lines.append({
            "text": str(text),
            "confidence": float(score),
            "boundingBox": to_original(box, scale, margin, width, height),
        })
    return drop_redundant(lines)


def recognize_without_detection(engine, image):
    """検出が空のときの一段落とし。切り抜き全体を 1 行の文字列として認識する。
    余白も拡大も付けない生の切り抜きを渡す（認識モデルは行の切り出し済み画像を前提とする）。"""
    result = engine(image, use_det=False, use_cls=False)
    texts = getattr(result, "txts", None) or []
    scores = getattr(result, "scores", None) or []
    height, width = image.shape[:2]
    lines = []
    for text, score in zip(texts, scores):
        if not str(text).strip() or float(score) < FALLBACK_MIN_SCORE:
            continue
        lines.append({
            "text": str(text),
            "confidence": float(score),
            "boundingBox": [[0, 0], [width, 0], [width, height], [0, height]],
        })
    return lines, result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    params = {
        "Det.engine_type": EngineType.ONNXRUNTIME,
        "Det.lang_type": LangDet.CH,
        "Det.model_type": ModelType.SMALL,
        "Det.ocr_version": OCRVersion.PPOCRV6,
        "Rec.engine_type": EngineType.ONNXRUNTIME,
        "Rec.lang_type": LangRec.CH,
        "Rec.model_type": ModelType.SMALL,
        "Rec.ocr_version": OCRVersion.PPOCRV6,
    }
    engine = RapidOCR(params=params)

    original = load_image(args.input)
    height, width = original.shape[:2]
    scaled, scale = upscale(original)
    prepared, margin = add_margin(scaled)

    result = engine(prepared)
    lines = build_lines(result, scale, margin, width, height)
    mode = "det"
    if not lines:
        lines, result = recognize_without_detection(engine, original)
        mode = "rec-only"

    payload = {
        "engine": "PP-OCRv6 Small",
        "elapsedSeconds": float(getattr(result, "elapse", 0) or 0),
        "mode": mode,
        "scale": scale,
        "margin": margin,
        "lines": lines,
    }
    with open(args.output, "w", encoding="utf-8") as output:
        json.dump(payload, output, ensure_ascii=False)


if __name__ == "__main__":
    main()
