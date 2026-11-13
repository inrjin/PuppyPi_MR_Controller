"""Recreate local QR fixtures with optional OpenCV, without camera access.

Run from the repository root:
  .local/experiments/venv/bin/python fixtures/study/generate_qr_fixture.py
"""

from pathlib import Path

import cv2
import numpy as np


def main():
    directory = Path(__file__).resolve().parent
    code = cv2.QRCodeEncoder_create().encode("KHCI2027:inspection")
    code = np.repeat(np.repeat(code, 4, axis=0), 4, axis=1)
    blank = np.full((480, 640), 255, dtype=np.uint8)
    target = blank.copy()
    height, width = code.shape
    top, left = (480 - height) // 2, (640 - width) // 2
    target[top:top + height, left:left + width] = code
    for name, image in (("target_qr.pgm", target), ("blank.pgm", blank)):
        if not cv2.imwrite(str(directory / name), image):
            raise RuntimeError("failed to write " + name)
    print("Created synthetic 640x480 target/blank fixtures; OpenCV", cv2.__version__)


if __name__ == "__main__":
    main()
