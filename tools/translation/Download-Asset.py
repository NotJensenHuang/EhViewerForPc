"""安装时按区间下载官方大文件，并在提交前核验完整文件。"""

from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
from pathlib import Path
import re
import sys
import time
import urllib.request


class HttpsRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if not newurl.startswith("https://"):
            raise ValueError("下载源必须继续使用 HTTPS")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def fetch_range(url, size, first, last):
    for attempt in range(4):
        try:
            opener = urllib.request.build_opener(HttpsRedirectHandler())
            request = urllib.request.Request(url, headers={"Range": f"bytes={first}-{last}"})
            with opener.open(request, timeout=15) as response:
                match = re.fullmatch(r"bytes (\d+)-(\d+)/(\d+)", response.headers.get("Content-Range", ""))
                if not match or tuple(map(int, match.groups())) != (first, last, size):
                    raise ValueError("下载源未返回所需的文件区间")
                data = response.read(last - first + 2)
                if len(data) != last - first + 1:
                    raise OSError("下载的文件区间不完整")
                return data
        except OSError:
            if attempt == 3:
                raise
            time.sleep(attempt + 1)


def download(url, destination, digest, size, chunk_bytes, workers):
    if not url.startswith("https://"):
        raise ValueError("下载源必须使用 HTTPS")
    fetch_range(url, size, 0, 0)
    temporary = destination.with_name(destination.name + ".ranges")
    try:
        with temporary.open("wb") as output, ThreadPoolExecutor(max_workers=workers) as pool:
            output.truncate(size)
            cursor = 0
            while cursor < size:
                pending = {}
                for _ in range(workers):
                    if cursor >= size:
                        break
                    first = cursor
                    cursor = min(size, cursor + chunk_bytes)
                    pending[pool.submit(fetch_range, url, size, first, cursor - 1)] = first
                for part in as_completed(pending):
                    output.seek(pending[part])
                    output.write(part.result())
                if cursor == size or cursor % (chunk_bytes * workers * 4) == 0:
                    print(f"{destination.name} {cursor}/{size}", flush=True)
        with temporary.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != digest:
                raise ValueError("完整文件的 SHA-256 校验失败")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    try:
        url, name, sha256, byte_length, chunk_size, worker_count = sys.argv[1:]
        download(url, Path(name), sha256, int(byte_length), int(chunk_size), int(worker_count))
    except (OSError, ValueError):
        print("并行下载或校验失败，将尝试普通下载", file=sys.stderr)
        sys.exit(1)
