#!/usr/bin/env python3
"""Renders an HTML file to a PNG with Playwright's Chromium (for looking at icon and layout previews).

    python3 tools/render_html.py <in.html> <out.png> [width] [height]
"""
import os
import sys

from playwright.sync_api import sync_playwright

# Libraries Chromium needs that this WSL image lacks (libasound and friends), kept beside Playwright.
DEPS = [os.path.expanduser("~/.playwright-deps"), os.path.expanduser("~/.local/playwright-deps")]


def main(src: str, dst: str, width: int = 1300, height: int = 1500) -> None:
    env = dict(os.environ)
    env["LD_LIBRARY_PATH"] = ":".join([d for d in DEPS if os.path.isdir(d)] + [env.get("LD_LIBRARY_PATH", "")])
    with sync_playwright() as p:
        browser = p.chromium.launch(env=env)
        page = browser.new_page(viewport={"width": width, "height": height}, device_scale_factor=1)
        page.goto("file://" + src)
        page.screenshot(path=dst, full_page=True)
        browser.close()


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], *(int(a) for a in sys.argv[3:5]))
