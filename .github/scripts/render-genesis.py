#!/usr/bin/env python3
# Renders a markdown document as a styled HTML page for the Pages site, in the
# Copilot Router's design family. Run by pages.yml at deploy time so a page can
# never drift from its markdown source.
#
#   render-genesis.py <source.md> <out.html> ["Page title"]
#
# THE PALETTE BELOW MUST MATCH docs/copilot-router.html. It did not for one
# release: the router moved to the light navy-and-orange theme and this file
# kept the dark teal one, so two pages on the same site disagreed about what the
# site looks like. Restyle both in the same change or neither.
import sys, markdown, pathlib, html as h

src, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
title = sys.argv[3] if len(sys.argv) > 3 else "Copilot Router Genesis Prompt"

md = src.read_text()

# Strip YAML frontmatter. Without this it renders as a paragraph of "title: ...
# description: ..." at the top of the page, which is what happened the first
# time a document carrying frontmatter was published here. The title is taken
# from it when the caller did not pass one, so the two cannot disagree.
if md.startswith("---"):
    end = md.find("\n---", 3)
    if end != -1:
        front, md = md[3:end], md[end + 4:].lstrip("\n")
        if len(sys.argv) <= 3:
            for line in front.splitlines():
                if line.startswith("title:"):
                    title = line.split(":", 1)[1].strip().strip('"\'')
                    break
body = markdown.markdown(md, extensions=["tables", "fenced_code"])

TEMPLATE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500&family=IBM+Plex+Sans:wght@400;500;600&display=swap">
<style>
  :root {
    color-scheme: light;
    --ground: #F4F7FA; --surface: #FFFFFF; --alt: #E8EEF5;
    --ink: #1B2733; --muted: #55636F; --faint: #78868F;
    --rule: #D4DCE4; --rule-strong: #AFBECB;
    --accent: #0A3B68; --accent-soft: #E4EDF6; --warm: #EE7623;
    --risk: #E38175;
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--ground); color: var(--ink);
    font-family: "IBM Plex Sans", system-ui, "Segoe UI", Roboto, Arial, sans-serif;
    font-size: 16px; line-height: 1.65; -webkit-font-smoothing: antialiased; }
  .wrap { max-width: 760px; margin: 0 auto; padding: clamp(2rem,5vw,4rem) clamp(1rem,4vw,2rem) 5rem; }
  h1, h2, h3 { font-family: "IBM Plex Sans", system-ui, "Segoe UI", Roboto, Arial, sans-serif;
    font-weight: 600; color: var(--accent); line-height: 1.15; text-wrap: balance; }
  h1::after { content: ""; display: block; width: 3.5rem; height: 3px;
    background: var(--warm); margin-top: 0.8rem; }
  h1 { font-size: clamp(1.7rem, 4vw, 2.4rem); letter-spacing: -0.015em; margin: 2.8rem 0 1rem;
       padding-top: 2.2rem; border-top: 2px solid var(--ink); }
  .wrap > h1:first-child { margin-top: 0; padding-top: 0; border-top: none; }
  h2 { font-size: 1.3rem; margin: 2.2rem 0 0.7rem; color: var(--ink); }
  p { margin: 0 0 1rem; max-width: 68ch; }
  li { max-width: 65ch; margin-bottom: 0.45rem; }
  ul, ol { padding-left: 1.3rem; margin: 0 0 1rem; }
  a { color: var(--accent); text-underline-offset: 2px; }
  a:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
  strong { color: var(--ink); }
  em { color: var(--muted); }
  code { font-family: "IBM Plex Mono", ui-monospace, Menlo, Consolas, monospace;
    font-size: 0.85em; background: var(--alt); border: 1px solid var(--rule);
    border-radius: 3px; padding: 0.08em 0.35em; }
  blockquote { margin: 1.2rem 0; padding: 0.9rem 1.2rem; background: var(--accent-soft);
    border-left: 3px solid var(--accent); border-radius: 0 3px 3px 0; }
  blockquote p { margin: 0.2rem 0; }
  hr { border: none; border-top: 1px solid var(--rule-strong); margin: 2.5rem 0; }
  .scroller { overflow-x: auto; border: 1px solid var(--rule); border-radius: 3px;
    background: var(--surface); margin: 0 0 1.2rem; }
  table { border-collapse: collapse; width: 100%; min-width: 540px; font-size: 0.88rem; }
  th, td { padding: 0.55rem 0.8rem; border-top: 1px solid var(--rule); text-align: left; vertical-align: top; }
  thead th { border-top: none; background: var(--alt);
    font-family: "IBM Plex Mono", ui-monospace, Menlo, Consolas, monospace;
    font-size: 0.68rem; font-weight: 500; letter-spacing: 0.1em; text-transform: uppercase; color: var(--muted); }
  footer { margin-top: 3.5rem; padding-top: 1.3rem; border-top: 1px solid var(--rule);
    font-family: "IBM Plex Mono", ui-monospace, Menlo, Consolas, monospace;
    font-size: 0.72rem; color: var(--faint); }
</style>
</head>
<body>
<div class="wrap">
__BODY__
<footer>The tool this prompt produces: <a href="copilot-router.html">the Copilot Router</a>. Where this document and the page disagree, the page is right.</footer>
</div>
</body>
</html>
"""

# wrap tables so wide ones scroll instead of stretching the page
body = body.replace("<table>", '<div class="scroller"><table>').replace("</table>", "</table></div>")
out.write_text(TEMPLATE.replace("__TITLE__", h.escape(title)).replace("__BODY__", body))
print("wrote", out, out.stat().st_size, "bytes")
