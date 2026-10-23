#!/usr/bin/env python3
"""
download_gbo.py - Class 10 English-medium question paper downloader for
https://www.gujaratboardonline.com/

WHAT IS CONFIRMED FROM THE INSPECTION ZIP
  * The homepage (https://www.gujaratboardonline.com/) IS the Class 10 page.
  * Papers are listed as plain <a> tags in static HTML (no AJAX, no forms,
    no pagination) inside <div id="final-papers-list">, "model-papers-list",
    "pre-board-list" and "unit-test-list".
  * Every link points to a DETAIL page: /papers/gseb-class-10-<...>.html
  * Medium is only encoded in the URL slug as a letter token (-e- / -g- / -h-
    / -ge- / -ghe-).

WHAT IS NOT CONFIRMED (not in the ZIP)
  * How a detail page exposes its PDF. No detail page was inspected.
    This script therefore uses a generic resolver (direct .pdf links,
    iframe/embed/object, data-* attributes, JS strings, Google Drive links,
    one-hop "download" buttons). Run  python download_gbo.py --probe  first
    and check what it reports.

Not bypassed: CAPTCHAs, Cloudflare challenges, logins. If one appears the
script stops and tells you.
"""

import argparse
import csv
import hashlib
import json
import logging
import os
import random
import re
import sys
import time
import zipfile
from html import unescape
from urllib.parse import urljoin, urlparse, parse_qs, unquote

import requests
from bs4 import BeautifulSoup

# --------------------------------------------------------------------------
# Configuration
# --------------------------------------------------------------------------
SITE = "https://www.gujaratboardonline.com/"
DEFAULT_BASE = "/storage/emulated/0/development/boards"
OUT_NAME = "GBO_Class10_English"
ZIP_NAME = "GBO_Class10_English.zip"
SECTION_IDS = {  # list container id -> section label (from the inspected HTML)
    "final-papers-list": "Final",
    "model-papers-list": "Model",
    "pre-board-list": "PreBoard",
    "unit-test-list": "UnitTest",
}
HEADERS = {
    "User-Agent": ("Mozilla/5.0 (Linux; Android 13; Mobile) AppleWebKit/537.36 "
                   "(KHTML, like Gecko) Chrome/124.0 Mobile Safari/537.36"),
    "Accept": "text/html,application/xhtml+xml,application/pdf,*/*;q=0.8",
    "Accept-Language": "en-IN,en;q=0.9",
}
TIMEOUT = (15, 90)          # connect, read
MAX_RETRIES = 4
MONTHS = {"jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"}
# medium letter tokens used in slugs -> set of mediums
MEDIUM_TOKENS = {
    "e": {"E"}, "g": {"G"}, "h": {"H"}, "ge": {"G", "E"}, "ghe": {"G", "H", "E"},
    "m": {"M"}, "u": {"U"}, "t": {"T"}, "te": {"TE"}, "o": {"O"},
}
MEDIUM_NAMES = {"E": "English", "G": "Gujarati", "H": "Hindi", "M": "Marathi",
                "U": "Urdu", "T": "Tamil", "TE": "Telugu", "O": "Odia"}
SKIP_HOSTS = ("googlesyndication", "doubleclick", "google-analytics", "googletagmanager",
              "facebook.com", "twitter.com", "whatsapp.com", "t.me", "instagram.com",
              "services.google.com", "cloudflare.com", "cloudflareinsights")

log = logging.getLogger("gbo")


class ChallengeError(Exception):
    """Cloudflare / CAPTCHA style block. We never try to bypass this."""


class FetchError(Exception):
    def __init__(self, msg, permanent=False):
        super().__init__(msg)
        self.permanent = permanent


# --------------------------------------------------------------------------
# Console tee + logging
# --------------------------------------------------------------------------
class Tee:
    def __init__(self, stream, fh):
        self.stream, self.fh = stream, fh

    def write(self, s):
        self.stream.write(s)
        self.fh.write(s)
        self.fh.flush()

    def flush(self):
        self.stream.flush()
        self.fh.flush()

    def isatty(self):
        return False


def setup_logging(base):
    os.makedirs(base, exist_ok=True)
    console_fh = open(os.path.join(base, "gbo_console.log"), "a", encoding="utf-8")
    sys.stdout = Tee(sys.__stdout__, console_fh)
    sys.stderr = Tee(sys.__stderr__, console_fh)
    log.setLevel(logging.DEBUG)
    fmt = logging.Formatter("%(asctime)s | %(levelname)s | %(message)s")
    fh = logging.FileHandler(os.path.join(base, "gbo_download.log"), encoding="utf-8")
    fh.setFormatter(fmt)
    fh.setLevel(logging.DEBUG)
    sh = logging.StreamHandler(sys.stdout)
    sh.setFormatter(fmt)
    sh.setLevel(logging.INFO)
    log.handlers[:] = [fh, sh]


# --------------------------------------------------------------------------
# Slug parsing / classification
# --------------------------------------------------------------------------
def classify(slug):
    """Derive year, session, subject folder and medium from the URL slug."""
    body = re.sub(r"^gseb-class-10-", "", slug)
    tokens = body.split("-")
    year = tokens[-1] if re.fullmatch(r"(19|20)\d\d", tokens[-1]) else ""
    session = next((t for t in tokens if t in MONTHS), "")

    # medium: first standalone medium-letter token
    medium_set, medium_tok = set(), ""
    for i, t in enumerate(tokens):
        if t in MEDIUM_TOKENS:
            medium_set, medium_tok = MEDIUM_TOKENS[t], t
            break
    if medium_set:
        medium = "+".join(MEDIUM_NAMES.get(m, m) for m in sorted(medium_set, key=lambda x: "EGH".find(x)))
    else:
        medium = "Unknown"

    b = body
    if re.search(r"sociology", b):
        subject = "Sociology"
    elif re.search(r"standard-mathematics|mathematics-standard|maths?-standard", b):
        subject = "Mathematics/Standard"
    elif re.search(r"basic-mathematics|mathematics-basic|maths?-basic", b):
        subject = "Mathematics/Basic"
    elif re.search(r"mathematics|(^|-)maths($|-)", b):
        subject = "Mathematics"
    elif re.search(r"social-science", b):
        subject = "Social Science"
    elif re.search(r"science", b):
        subject = "Science"
    elif re.search(r"english", b):
        subject = "English"
    elif re.search(r"gujarati|gujrati", b):
        subject = "Gujarati"
    elif re.search(r"hindi", b):
        subject = "Hindi"
    elif re.search(r"sanskrit", b):
        subject = "Sanskrit"
    elif re.search(r"sharirik|yog|physical", b):
        subject = "Physical Education"
    elif re.search(r"computer|(^|-)it-ites", b):
        subject = "Computer"
    else:
        subject = "Other"

    return {
        "year": year, "session": session, "subject": subject, "medium": medium,
        "medium_set": medium_set, "medium_token": medium_tok, "short": body,
    }


def safe_name(s):
    return re.sub(r"[^A-Za-z0-9._()+-]+", "_", s).strip("_")


# --------------------------------------------------------------------------
# HTTP layer
# --------------------------------------------------------------------------
class Http:
    def __init__(self, delay):
        self.s = requests.Session()
        self.s.headers.update(HEADERS)
        self.delay = delay
        self.challenges = 0

    def _sleep(self):
        time.sleep(self.delay * random.uniform(0.8, 1.4))

    def _check_challenge(self, r, snippet=""):
        low = (snippet or "").lower()
        if r.status_code in (403, 429, 503) and (
                r.headers.get("cf-mitigated") or "just a moment" in low
                or "captcha" in low or "attention required" in low):
            self.challenges += 1
            raise ChallengeError(
                f"HTTP {r.status_code} bot/CAPTCHA challenge at {r.url} - not bypassing")
        if "g-recaptcha" in low or "cf-turnstile" in low or "hcaptcha" in low:
            self.challenges += 1
            raise ChallengeError(f"CAPTCHA widget on {r.url} - not bypassing")

    def get_html(self, url, referer=None):
        """GET a page as text with retries. Returns (text, final_url)."""
        last = None
        for attempt in range(1, MAX_RETRIES + 1):
            self._sleep()
            try:
                hdr = {"Referer": referer} if referer else {}
                r = self.s.get(url, headers=hdr, timeout=TIMEOUT)
                log.info("GET %s -> %s (%d bytes)", url, r.status_code, len(r.content))
                self._check_challenge(r, r.text[:3000])
                if r.status_code in (404, 410):
                    raise FetchError(f"HTTP {r.status_code}", permanent=True)
                if r.status_code in (429, 500, 502, 503, 504):
                    ra = r.headers.get("Retry-After", "")
                    wait = int(ra) if ra.isdigit() else 3 * attempt
                    raise FetchError(f"HTTP {r.status_code} (retry in {wait}s)")
                if r.status_code >= 400:
                    raise FetchError(f"HTTP {r.status_code}", permanent=True)
                if not r.text.strip():
                    raise FetchError("empty response body")
                return r.text, r.url
            except ChallengeError:
                raise
            except FetchError as e:
                last = e
                if e.permanent:
                    raise
            except requests.RequestException as e:
                last = FetchError(f"{type(e).__name__}: {e}")
            log.warning("retry %d/%d for %s: %s", attempt, MAX_RETRIES, url, last)
            time.sleep(2 * attempt)
        raise FetchError(f"gave up after {MAX_RETRIES} attempts: {last}")

    def get_pdf_or_html(self, url, dest, referer=None):
        """
        Download url. If the body is a PDF, write it to dest (atomic via .part)
        and return ("pdf", size). Otherwise return ("html", text, final_url).
        """
        last = None
        for attempt in range(1, MAX_RETRIES + 1):
            self._sleep()
            part = dest + ".part"
            try:
                hdr = {"Referer": referer} if referer else {}
                with self.s.get(url, headers=hdr, timeout=TIMEOUT, stream=True) as r:
                    ctype = r.headers.get("Content-Type", "")
                    log.info("GET %s -> %s [%s]", url, r.status_code, ctype)
                    if r.status_code in (404, 410):
                        raise FetchError(f"HTTP {r.status_code}", permanent=True)
                    if r.status_code in (403, 429, 503):
                        peek = r.raw.read(3000, decode_content=True) or b""
                        self._check_challenge(r, peek.decode("utf-8", "ignore"))
                    if r.status_code in (429, 500, 502, 503, 504):
                        raise FetchError(f"HTTP {r.status_code}")
                    if r.status_code >= 400:
                        raise FetchError(f"HTTP {r.status_code}", permanent=True)

                    it = r.iter_content(65536)
                    first = next(it, b"")
                    if first[:5] == b"%PDF-":
                        size = 0
                        with open(part, "wb") as fh:
                            fh.write(first)
                            size += len(first)
                            for chunk in it:
                                fh.write(chunk)
                                size += len(chunk)
                        clen = r.headers.get("Content-Length")
                        if clen and clen.isdigit() and int(clen) != size \
                                and not r.headers.get("Content-Encoding"):
                            raise FetchError(f"truncated download {size}/{clen}")
                        if size < 1024:
                            raise FetchError(f"PDF suspiciously small ({size} bytes)")
                        os.replace(part, dest)
                        return ("pdf", size)
                    # not a PDF: read some text so the caller can inspect it
                    buf = first
                    for chunk in it:
                        buf += chunk
                        if len(buf) > 2_000_000:
                            break
                    text = buf.decode(r.encoding or "utf-8", "replace")
                    self._check_challenge(r, text[:3000])
                    return ("html", text, r.url)
            except ChallengeError:
                raise
            except FetchError as e:
                last = e
                if e.permanent:
                    raise
            except requests.RequestException as e:
                last = FetchError(f"{type(e).__name__}: {e}")
            finally:
                if os.path.exists(part):
                    try:
                        os.remove(part)
                    except OSError:
                        pass
            log.warning("retry %d/%d for %s: %s", attempt, MAX_RETRIES, url, last)
            time.sleep(2 * attempt)
        raise FetchError(f"gave up after {MAX_RETRIES} attempts: {last}")


# --------------------------------------------------------------------------
# Discovery (confirmed mechanism: static HTML lists on the homepage)
# --------------------------------------------------------------------------
def parse_listing(html, base_url):
    soup = BeautifulSoup(html, "html.parser")
    papers, seen = [], set()
    for list_id, section in SECTION_IDS.items():
        box = soup.find(id=list_id)
        if box is None:
            log.warning("list container #%s not found in page", list_id)
            continue
        for a in box.find_all("a", href=True):
            url = urljoin(base_url, a["href"])
            if "/papers/" not in url or url in seen:
                continue
            seen.add(url)
            slug = os.path.basename(urlparse(url).path)
            slug = re.sub(r"\.html?$", "", slug)
            info = classify(slug)
            info.update({
                "section": section, "slug": slug, "detail_url": url,
                "title": a.get_text(" ", strip=True),
            })
            papers.append(info)
    return papers


def is_class10(p):
    return p["slug"].startswith("gseb-class-10-")


def select_english(p, include_unknown, exclude_multi, include_solutions=False):
    if "solutions" in p["slug"] and not include_solutions:
        return False, "solutions/answer-key page (use --include-solutions)"
    ms = p["medium_set"]
    if not ms:
        return include_unknown, "medium not encoded in slug"
    if "E" not in ms:
        return False, "non-English medium"
    if len(ms) > 1 and exclude_multi:
        return False, "multi-medium paper (excluded by flag)"
    return True, "English medium" if len(ms) == 1 else "multi-medium incl. English"


# --------------------------------------------------------------------------
# PDF resolver for detail pages (GENERIC - detail page structure unverified)
# --------------------------------------------------------------------------
PDF_RE = re.compile(r"""(?:https?:)?(?:\\?/\\?/)[^\s"'<>\\()]+?\.pdf(?:\?[^\s"'<>\\()]*)?""", re.I)
REL_PDF_RE = re.compile(r"""["'(]((?:\.{0,2}/)?[^\s"'<>()\\]+?\.pdf(?:\?[^\s"'<>()]*)?)["')]""", re.I)
DRIVE_RE = re.compile(r"drive\.google\.com/(?:file/d/|open\?id=|uc\?(?:[^\"'\s]*&)?id=)([\w-]{20,})", re.I)


def _host_ok(url):
    h = urlparse(url).netloc.lower()
    if "drive.google.com" in h or "docs.google.com" in h:
        return True
    return not any(s in h for s in SKIP_HOSTS)


def _unwrap_viewer(url):
    """docs.google.com/viewer?url=..., pdf.js ?file=..."""
    q = parse_qs(urlparse(url).query)
    for k in ("url", "file", "src", "pdf"):
        if k in q and q[k]:
            inner = unquote(q[k][0])
            if inner.startswith("http") or inner.startswith("/"):
                return inner
    return url


def extract_candidates(html, base_url):
    """Return (pdf_candidates, hop_pages). Both ordered, de-duplicated."""
    html = unescape(html).replace("\\/", "/")
    soup = BeautifulSoup(html, "html.parser")
    pdfs, hops = [], []

    def add_pdf(u):
        if not u:
            return
        u = u.replace("\\/", "/").strip()
        u = urljoin(base_url, _unwrap_viewer(urljoin(base_url, u)))
        m = DRIVE_RE.search(u)
        if m:
            u = f"https://drive.google.com/uc?export=download&id={m.group(1)}"
        elif not re.search(r"\.pdf(\?|$|#)", u, re.I):
            return
        if _host_ok(u) and u not in pdfs:
            pdfs.append(u)

    # 1. explicit tags
    for tag in soup.find_all(["a", "iframe", "embed", "object", "source", "link"]):
        for attr in ("href", "src", "data", "data-src", "data-href", "data-url",
                     "data-pdf", "data-file", "data-download"):
            v = tag.get(attr)
            if v:
                add_pdf(v)
    # 2. any attribute value on any tag (onclick, data-*)
    for tag in soup.find_all(True):
        for k, v in tag.attrs.items():
            if isinstance(v, str) and (".pdf" in v.lower() or "drive.google" in v.lower()):
                for m in PDF_RE.findall(v):
                    add_pdf(m if m.startswith("http") else "https:" + m if m.startswith("//") else m)
                for m in DRIVE_RE.finditer(v):
                    add_pdf(m.group(0) if m.group(0).startswith("http") else "https://" + m.group(0))
    # 3. raw text (inline JS, JSON)
    for m in PDF_RE.findall(html):
        m = m.replace("\\/", "/")
        add_pdf("https:" + m if m.startswith("//") else m)
    for m in REL_PDF_RE.findall(html):
        add_pdf(m)
    for m in DRIVE_RE.finditer(html):
        add_pdf("https://" + m.group(0))
    # 4. meta refresh
    for meta in soup.find_all("meta", attrs={"http-equiv": re.compile("refresh", re.I)}):
        mm = re.search(r"url=(.+)", meta.get("content", ""), re.I)
        if mm:
            u = urljoin(base_url, mm.group(1).strip("'\" "))
            if u not in hops:
                hops.append(u)

    # hop pages: same-site anchors that look like a download/view button
    site_host = urlparse(SITE).netloc
    for a in soup.find_all("a", href=True):
        label = " ".join([a.get_text(" ", strip=True), " ".join(a.get("class", [])),
                          a.get("id", ""), a.get("title", "")]).lower()
        if re.search(r"download|view pdf|open pdf|get pdf|\bpdf\b", label):
            u = urljoin(base_url, a["href"])
            if urlparse(u).netloc == site_host and "/papers/" not in u \
                    and not re.search(r"\.(png|jpe?g|gif|css|js)(\?|$)", u, re.I) \
                    and u != base_url and u not in hops and u not in pdfs:
                hops.append(u)
    return pdfs, hops


def score_candidate(url, slug):
    name = os.path.basename(urlparse(url).path).lower()
    name = re.sub(r"[^a-z0-9]+", "-", name)
    toks = [t for t in slug.split("-") if len(t) > 1 and t not in ("gseb", "class")]
    return sum(1 for t in toks if t in name)


def resolve_and_download(http, paper, dest, debug_dir, state):
    """Find the PDF for a detail page and save it to dest. Returns pdf_url."""
    slug, detail = paper["slug"], paper["detail_url"]
    cached = state.get(detail, {}).get("pdf_url")
    queue = []  # (url, depth, referer)
    notes = []

    if cached:
        queue.append((cached, 0, detail))
    else:
        html, final = http.get_html(detail, referer=SITE)
        pdfs, hops = extract_candidates(html, final)
        log.info("  %s: %d direct candidate(s), %d hop page(s)", slug, len(pdfs), len(hops))
        if len(pdfs) > 1:
            pdfs.sort(key=lambda u: -score_candidate(u, slug))
            top = [score_candidate(u, slug) for u in pdfs[:2]]
            if top[0] == top[1]:
                notes.append("ambiguous: " + " | ".join(pdfs[:5]))
                log.warning("  ambiguous candidates for %s: %s", slug, pdfs[:5])
        queue += [(u, 0, final) for u in pdfs[:5]]
        queue += [(u, 1, final) for u in hops[:3]]
        if not queue:
            os.makedirs(debug_dir, exist_ok=True)
            with open(os.path.join(debug_dir, slug + ".html"), "w", encoding="utf-8") as fh:
                fh.write(html)
            raise FetchError(f"NO_PDF_FOUND on detail page (saved {debug_dir}/{slug}.html)",
                             permanent=True)

    tried = set()
    while queue:
        url, depth, ref = queue.pop(0)
        if url in tried:
            continue
        tried.add(url)
        try:
            res = http.get_pdf_or_html(url, dest, referer=ref)
        except FetchError as e:
            log.warning("  candidate failed %s: %s", url, e)
            continue
        if res[0] == "pdf":
            log.info("  PDF OK %s (%d bytes) <- %s", os.path.basename(dest), res[1], url)
            state[detail] = {"pdf_url": url, "notes": notes}
            return url, "; ".join(notes)
        # got HTML: maybe an interstitial page
        _, text, final = res
        log.info("  candidate returned HTML, not PDF: %s", url)
        if depth < 2:
            more, _ = extract_candidates(text, final)
            for u in more[:3]:
                queue.insert(0, (u, depth + 1, final))
        if "drive.google.com" in url:
            os.makedirs(debug_dir, exist_ok=True)
            with open(os.path.join(debug_dir, slug + "_drive.html"), "w", encoding="utf-8") as fh:
                fh.write(text)
    raise FetchError("no candidate returned a valid PDF (see log)", permanent=True)


# --------------------------------------------------------------------------
# Output files
# --------------------------------------------------------------------------
def target_path(out_dir, paper, used):
    subj = paper["subject"].split("/")
    folder = os.path.join(out_dir, *[safe_name(x) for x in subj])
    parts = [paper["year"] or "unknown-year", paper["session"], paper["section"],
             paper["short"]]
    name = safe_name("_".join(p for p in parts if p)) + ".pdf"
    path = os.path.join(folder, name)
    # never let two different papers share a filename
    key = os.path.abspath(path)
    if key in used and used[key] != paper["detail_url"]:
        h = hashlib.sha1(paper["detail_url"].encode()).hexdigest()[:6]
        path = path[:-4] + f"_{h}.pdf"
    used[os.path.abspath(path)] = paper["detail_url"]
    return path


def is_valid_pdf(path):
    try:
        if os.path.getsize(path) < 1024:
            return False
        with open(path, "rb") as fh:
            return fh.read(5) == b"%PDF-"
    except OSError:
        return False


def write_csv(base, papers):
    cols = ["section", "year", "session", "subject", "medium", "selected", "select_reason",
            "title", "detail_url", "pdf_url", "status", "file", "note"]
    with open(os.path.join(base, "gbo_discovered_papers.csv"), "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        for p in papers:
            w.writerow(p)


def write_failed(base, papers):
    bad = [p for p in papers if p.get("status", "").startswith("failed")]
    with open(os.path.join(base, "gbo_failed_downloads.txt"), "w", encoding="utf-8") as fh:
        for p in bad:
            fh.write(f"{p['detail_url']}\t{p['status']}\t{p.get('note', '')}\n")
        if not bad:
            fh.write("(no failures)\n")


def make_zip(base, out_dir):
    zpath = os.path.join(base, ZIP_NAME)
    tmp = zpath + ".tmp"
    n = 0
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(out_dir):
            for f in sorted(files):
                if not f.lower().endswith(".pdf"):
                    continue
                full = os.path.join(root, f)
                if not is_valid_pdf(full):
                    log.warning("ZIP: skipping invalid PDF %s", full)
                    continue
                z.write(full, os.path.relpath(full, base))
                n += 1
    with zipfile.ZipFile(tmp) as z:
        bad = z.testzip()
    if bad:
        log.error("ZIP integrity problem at %s", bad)
    os.replace(tmp, zpath)
    log.info("ZIP created: %s (%d PDFs, %.1f MB)", zpath, n, os.path.getsize(zpath) / 1e6)
    return n


# --------------------------------------------------------------------------
# Modes
# --------------------------------------------------------------------------
def load_listing(http, base, list_html):
    if list_html:
        log.info("Reading listing from local file %s", list_html)
        with open(list_html, encoding="utf-8", errors="replace") as fh:
            html, final = fh.read(), SITE
    else:
        html, final = http.get_html(SITE)
    with open(os.path.join(base, "gbo_discovery_debug.html"), "w", encoding="utf-8") as fh:
        fh.write(html)
    papers = [p for p in parse_listing(html, final) if is_class10(p)]
    return papers


def probe(http, base, papers):
    """Read-only diagnostics: show how 3 detail pages expose their PDF."""
    d = os.path.join(base, "gbo_probe")
    os.makedirs(d, exist_ok=True)
    picks = []
    for want in ("english-second-language-16-e-mar-2025", "science-11-e-mar-2023",
                 "pre-board-science-11-060226-2026"):
        for p in papers:
            if p["slug"].endswith(want):
                picks.append(p)
                break
    report = []
    for p in picks or papers[:3]:
        html, final = http.get_html(p["detail_url"], referer=SITE)
        fn = os.path.join(d, p["slug"] + ".html")
        with open(fn, "w", encoding="utf-8") as fh:
            fh.write(html)
        pdfs, hops = extract_candidates(html, final)
        soup = BeautifulSoup(html, "html.parser")
        scripts = [s.get("src") for s in soup.find_all("script", src=True)]
        forms = [(f.get("action"), f.get("method")) for f in soup.find_all("form")]
        frames = [f.get("src") for f in soup.find_all(["iframe", "embed", "object"])]
        report.append({
            "detail_url": p["detail_url"], "saved_html": fn, "html_bytes": len(html),
            "pdf_candidates": pdfs, "hop_pages": hops, "forms": forms,
            "iframes_embeds": frames, "external_scripts": scripts,
            "has_captcha_markers": bool(re.search(r"recaptcha|turnstile|hcaptcha", html, re.I)),
        })
    with open(os.path.join(d, "probe_report.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)
    print(json.dumps(report, indent=2))
    log.info("Probe finished. Send me %s/probe_report.json (and one saved .html) if downloads fail.", d)


def run(args):
    base = args.base
    if not os.path.isdir(base):
        try:
            os.makedirs(base, exist_ok=True)
        except OSError:
            base = os.getcwd()
    setup_logging(base)
    log.info("=== download_gbo.py start | base=%s ===", base)
    out_dir = os.path.join(base, OUT_NAME)
    os.makedirs(out_dir, exist_ok=True)
    http = Http(args.delay)
    state_path = os.path.join(base, "gbo_state.json")
    state = json.load(open(state_path)) if os.path.exists(state_path) else {}
    papers = []

    try:
        papers = load_listing(http, base, args.list_html)
        log.info("Discovered %d Class 10 paper detail pages", len(papers))
        by_sec = {}
        for p in papers:
            by_sec[p["section"]] = by_sec.get(p["section"], 0) + 1
        log.info("By section: %s", by_sec)

        if args.probe:
            probe(http, base, papers)
            return

        sections = {s.strip().lower() for s in args.sections.split(",")} if args.sections else None
        years = {y.strip() for y in args.years.split(",")} if args.years else None
        for p in papers:
            ok, why = select_english(p, args.include_unknown, args.exclude_multi, args.include_solutions)
            if sections and p["section"].lower() not in sections:
                ok, why = False, "section filtered"
            if years and p["year"] not in years:
                ok, why = False, "year filtered"
            p["selected"], p["select_reason"] = ok, why
            p["status"], p["file"], p["pdf_url"], p["note"] = ("not selected" if not ok else "pending"), "", "", ""
        sel = [p for p in papers if p["selected"]]
        log.info("English-medium selected: %d of %d", len(sel), len(papers))
        write_csv(base, papers)
        if args.limit:
            sel = sel[:args.limit]
        if args.dry_run:
            for p in sel:
                print(p["year"], p["session"], p["subject"], p["medium"], p["slug"])
            log.info("Dry run only - nothing downloaded.")
            return

        used, counts = {}, {"downloaded": 0, "skipped": 0, "failed": 0}
        for i, p in enumerate(sel, 1):
            dest = target_path(out_dir, p, used)
            p["file"] = os.path.relpath(dest, base)
            log.info("[%d/%d] %s", i, len(sel), p["slug"])
            if is_valid_pdf(dest):
                p["status"], p["pdf_url"] = "skipped (already valid)", state.get(p["detail_url"], {}).get("pdf_url", "")
                counts["skipped"] += 1
                log.info("  skip, valid file exists")
                continue
            if os.path.exists(dest):
                log.warning("  existing file invalid, re-downloading")
                os.remove(dest)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            try:
                url, note = resolve_and_download(http, p, dest, os.path.join(base, "gbo_debug"), state)
                if not is_valid_pdf(dest):
                    raise FetchError("post-download validation failed")
                p["status"], p["pdf_url"], p["note"] = "downloaded", url, note
                counts["downloaded"] += 1
            except ChallengeError as e:
                log.error("STOPPING: %s", e)
                p["status"], p["note"] = "failed: challenge", str(e)
                counts["failed"] += 1
                break
            except FetchError as e:
                log.error("  FAILED %s: %s", p["slug"], e)
                p["status"], p["note"] = "failed", str(e)
                p["status"] = "failed: " + str(e)[:80]
                counts["failed"] += 1
            except Exception as e:  # keep going on odd errors
                log.exception("  unexpected error")
                p["status"], p["note"] = "failed: unexpected", repr(e)
                counts["failed"] += 1
            with open(state_path, "w") as fh:
                json.dump(state, fh)
        log.info("SUMMARY downloaded=%d skipped=%d failed=%d", counts["downloaded"],
                 counts["skipped"], counts["failed"])
    except ChallengeError as e:
        log.error("STOPPING: %s", e)
    except KeyboardInterrupt:
        log.warning("Interrupted - progress is saved; re-run to resume.")
    except FetchError as e:
        log.error("Could not load listing: %s", e)
    finally:
        if papers:
            write_csv(base, papers)
            write_failed(base, papers)
            with open(state_path, "w") as fh:
                json.dump(state, fh)
        if not (args.probe or args.dry_run or args.no_zip):
            n = make_zip(base, out_dir)
            if n == 0:
                log.warning("ZIP contains 0 PDFs - see gbo_failed_downloads.txt / gbo_download.log")
        log.info("=== done ===")


def main():
    ap = argparse.ArgumentParser(description="GujaratBoardOnline Class 10 English-medium downloader")
    ap.add_argument("--base", default=DEFAULT_BASE, help="working directory")
    ap.add_argument("--probe", action="store_true", help="read-only diagnostics of 3 detail pages")
    ap.add_argument("--dry-run", action="store_true", help="list selected papers, download nothing")
    ap.add_argument("--limit", type=int, default=0, help="only process first N selected papers")
    ap.add_argument("--delay", type=float, default=1.5, help="base delay between requests (s)")
    ap.add_argument("--sections", default="", help="comma list: final,model,preboard,unittest")
    ap.add_argument("--years", default="", help="comma list of years, e.g. 2025,2024")
    ap.add_argument("--include-unknown", action="store_true",
                    help="also download papers whose slug has no medium letter")
    ap.add_argument("--exclude-multi", action="store_true",
                    help="exclude shared papers like -ge- / -ghe- (Sanskrit etc.)")
    ap.add_argument("--include-solutions", action="store_true",
                    help="also include 'solutions-part-N' pages (answer keys, not question papers)")
    ap.add_argument("--list-html", default="", help="parse a saved homepage instead of fetching")
    ap.add_argument("--no-zip", action="store_true")
    run(ap.parse_args())


if __name__ == "__main__":
    main()
