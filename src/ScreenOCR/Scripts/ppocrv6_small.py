import argparse
import json

from rapidocr import EngineType, LangDet, LangRec, ModelType, OCRVersion, RapidOCR


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
    result = RapidOCR(params=params)(args.input)
    boxes = result.boxes if result.boxes is not None else []
    texts = result.txts if result.txts is not None else []
    scores = result.scores if result.scores is not None else []
    lines = []
    for box, text, score in zip(boxes, texts, scores):
        lines.append({
            "text": str(text),
            "confidence": float(score),
            "boundingBox": box.tolist() if hasattr(box, "tolist") else box,
        })

    payload = {
        "engine": "PP-OCRv6 Small",
        "elapsedSeconds": float(result.elapse or 0),
        "lines": lines,
    }
    with open(args.output, "w", encoding="utf-8") as output:
        json.dump(payload, output, ensure_ascii=False)


if __name__ == "__main__":
    main()
