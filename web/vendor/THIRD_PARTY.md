# Chat browser dependencies

The chat interface serves these files locally. It does not request JavaScript or
stylesheets from a CDN at runtime. The upstream browser builds are unmodified;
`marked.min.js` uses the upstream UMD filename shown below.

| Dependency | Version | Local files | Upstream files | License |
| --- | --- | --- | --- | --- |
| [Marked](https://github.com/markedjs/marked) | 18.1.0 | `marked.min.js` | `lib/marked.umd.js` | MIT, in `marked.LICENSE` |
| [DOMPurify](https://github.com/cure53/DOMPurify) | 3.4.16 | `purify.min.js` | `dist/purify.min.js` | Apache 2.0 or MPL 2.0, in `dompurify.LICENSE` and `dompurify.LICENSE-MPL` |
| [Highlight.js CDN assets](https://github.com/highlightjs/cdn-release) | 11.12.0 | `highlight.min.js`, `highlight.css` | `highlight.min.js`, `styles/github-dark.min.css` | BSD 3-Clause, in `highlight.LICENSE` |

Official npm tarballs:

- [marked-18.1.0.tgz](https://registry.npmjs.org/marked/-/marked-18.1.0.tgz)
- [dompurify-3.4.16.tgz](https://registry.npmjs.org/dompurify/-/dompurify-3.4.16.tgz)
- [cdn-assets-11.12.0.tgz](https://registry.npmjs.org/@highlightjs/cdn-assets/-/cdn-assets-11.12.0.tgz)

To regenerate, run from the project root:

```powershell
.\.venv\Scripts\python.exe scripts/vendor-chat-assets.py
```

The script pins SHA-512 integrity values from the official npm registry,
verifies each complete archive before reading it, and reads only the listed
regular-file members. It never extracts archives into the filesystem.

The browser globals are `marked`, `DOMPurify`, and `hljs`. Markdown must be
sanitized with DOMPurify before being inserted into the page.
