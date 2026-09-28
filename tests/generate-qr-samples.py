"""tests/fixtures の QR コード画像を生成する。

OpenCV（RapidOCR の依存として既に入っている）の QR エンコーダーだけを使い、
ScreenOCR 側のデコーダー（ZXing.Net）とは別実装で符号化する。生成物はリポジトリへ
コミットするため、テスト実行時に Python は不要。

    python tests\\generate-qr-samples.py
"""

from __future__ import annotations

import pathlib

import cv2
import numpy as np

FIXTURES = pathlib.Path(__file__).resolve().parent / "fixtures"

URL = "https://github.com/ytmknd/ScreenOCR"
JAPANESE = "画面の QR コードを読み取ります。"
KANJI = "日本語のテスト"


def matrix(text: str, *, kanji: bool = False) -> np.ndarray:
    """0=黒 / 255=白 のモジュール行列を返す（余白なし）。"""
    if kanji:
        params = cv2.QRCodeEncoder_Params()
        params.mode = cv2.QRCodeEncoder_MODE_KANJI
        # OpenCV の Kanji モードは Shift_JIS のバイト列を受け取る。
        return cv2.QRCodeEncoder_create(params).encode(text.encode("shift_jis"))
    return cv2.QRCodeEncoder_create().encode(text)


def render(modules: np.ndarray, module_px: int = 6, quiet_modules: int = 4) -> np.ndarray:
    image = np.kron(modules, np.ones((module_px, module_px), np.uint8))
    border = quiet_modules * module_px
    return cv2.copyMakeBorder(image, border, border, border, border, cv2.BORDER_CONSTANT, value=255)


def save(name: str, image: np.ndarray) -> None:
    path = FIXTURES / name
    cv2.imwrite(str(path), image)
    print(f"{path} ({image.shape[1]}x{image.shape[0]})")


def main() -> None:
    FIXTURES.mkdir(parents=True, exist_ok=True)
    save("qr-url.png", render(matrix(URL)))
    save("qr-japanese-utf8.png", render(matrix(JAPANESE)))
    save("qr-kanji-sjis.png", render(matrix(KANJI, kanji=True)))
    # 1 モジュール 2 px の小さな QR。拡大再試行の経路を通す。
    save("qr-small.png", render(matrix(URL), module_px=2))

    left = render(matrix(URL))
    right = render(matrix(JAPANESE))
    height = max(left.shape[0], right.shape[0])
    canvas = np.full((height, left.shape[1] + right.shape[1], 1), 255, np.uint8)
    canvas[: left.shape[0], : left.shape[1], 0] = left
    canvas[: right.shape[0], left.shape[1] :, 0] = right
    save("qr-multi.png", canvas)


if __name__ == "__main__":
    main()
