r"""Refresh the chat UI's pinned browser assets from official npm tarballs.

Run with the project's Python environment:
    .venv\Scripts\python.exe scripts/vendor-chat-assets.py

Every tarball must match its pinned SHA-512 integrity before any file is read.
Only explicitly named regular files are copied; archives are never extracted.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import io
from pathlib import Path
import tarfile
import tempfile

import httpx


DESTINATION = Path(__file__).resolve().parents[1] / "web" / "vendor"
PACKAGES = (
    {
        "name": "marked",
        "version": "18.1.0",
        "url": "https://registry.npmjs.org/marked/-/marked-18.1.0.tgz",
        "integrity": "sha512-PamYXWWWg2nboG3oX5Ffzpy3EFZROqZJK9hSlMmUnekp7TxPs5DcT95vwd46m73/exfKCRVMtgXoPMI4FEJL6w==",
        "files": {
            "package/lib/marked.umd.js": "marked.min.js",
            "package/LICENSE": "marked.LICENSE",
        },
    },
    {
        "name": "dompurify",
        "version": "3.4.16",
        "url": "https://registry.npmjs.org/dompurify/-/dompurify-3.4.16.tgz",
        "integrity": "sha512-sqo+pNp3qRhCIpbgRi1y8Tgk27Bo2Ry7w0dC1NBeNTdZChWjz9Xb/KOoZbRP/R6pQZ80Qw8YhXw13hWWBbMRnQ==",
        "files": {
            "package/dist/purify.min.js": "purify.min.js",
            "package/LICENSE": "dompurify.LICENSE",
            "package/LICENSE-MPL": "dompurify.LICENSE-MPL",
        },
    },
    {
        "name": "@highlightjs/cdn-assets",
        "version": "11.12.0",
        "url": "https://registry.npmjs.org/@highlightjs/cdn-assets/-/cdn-assets-11.12.0.tgz",
        "integrity": "sha512-KvOKXODaiFmId9xaq3xc5xCL66wVLUuOngDbO9B/kewbFTqdGbn2nJxNhN3H5R1cgDTVj6R8vH0zgiNDEGjpDw==",
        "files": {
            "package/highlight.min.js": "highlight.min.js",
            "package/styles/github-dark.min.css": "highlight.css",
            "package/LICENSE": "highlight.LICENSE",
        },
    },
)


def read_package(client: httpx.Client, package: dict) -> dict[str, bytes]:
    response = client.get(package["url"])
    response.raise_for_status()
    archive_bytes = response.content
    actual = "sha512-" + base64.b64encode(
        hashlib.sha512(archive_bytes).digest()
    ).decode("ascii")
    if not hmac.compare_digest(actual, package["integrity"]):
        raise ValueError(f"Integrity mismatch for {package['name']} {package['version']}")

    output = {}
    with tarfile.open(fileobj=io.BytesIO(archive_bytes), mode="r:gz") as archive:
        for member_name, filename in package["files"].items():
            member = archive.getmember(member_name)
            if not member.isfile():
                raise ValueError(f"Expected a regular file: {member_name}")
            stream = archive.extractfile(member)
            if stream is None:
                raise ValueError(f"Unable to read: {member_name}")
            with stream:
                output[filename] = stream.read()
    return output


def main() -> None:
    assets = {}
    with httpx.Client(timeout=60, follow_redirects=False) as client:
        for package in PACKAGES:
            assets.update(read_package(client, package))
            print(f"Verified {package['name']} {package['version']}")

    DESTINATION.mkdir(parents=True, exist_ok=True)
    for filename, contents in assets.items():
        # Replace complete files, so an interrupted write cannot truncate an asset.
        with tempfile.NamedTemporaryFile(dir=DESTINATION, delete=False) as temporary:
            temporary.write(contents)
            temporary_path = Path(temporary.name)
        try:
            temporary_path.replace(DESTINATION / filename)
        finally:
            temporary_path.unlink(missing_ok=True)
    print(f"Saved {len(assets)} assets and license files in {DESTINATION}")


if __name__ == "__main__":
    main()
