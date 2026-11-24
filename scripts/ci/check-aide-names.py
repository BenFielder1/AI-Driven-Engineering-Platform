#!/usr/bin/env python3
"""FR-329 (0.8.0): no leftover `aide` names.

Scans every tracked file (skipping the frozen `docs/0.*` folders and `docs/history`) for the old product name as a word or
identifier part: `aide`, `Aide` or `AIDE` not preceded by a letter, or `Aide`/`AIDE` after a lowercase letter or digit
(a camelCase part), and not followed by a lowercase letter; plus the plural `AIDEs`. So it matches `aide`, `aide_merges`,
`AideDbContext`, `asAide`, `X-AIDE-Event` and `.aide/`, but not English words such as "aided" or "braided".

Each hit's token (the run of identifier, path and URL characters around it) must be allowed by an entry of
scripts/ci/aide-allowlist.txt. An entry is one line of four tab-separated fields:

    <path regex>  <token regex>  <removal version>  <reason>

Both regexes must match in full (the path is repo-relative, with forward slashes). Lines starting with `#` and blank
lines are ignored. Entries no hit uses are reported too, so the allowlist shrinks with the code.

Usage: python3 scripts/ci/check-aide-names.py              (from the repo root)
       python3 scripts/ci/check-aide-names.py --self-test  (tests of the matcher)
"""
import re
import subprocess
import sys

ALLOWLIST = 'scripts/ci/aide-allowlist.txt'
NAME = re.compile(r'(?<![A-Za-z])(aide|Aide|AIDE)(?![a-z])|(?<=[a-z0-9])(Aide|AIDE)(?![a-z])|(?<![A-Za-z])AIDEs(?![a-z])')
TOKEN_CHARS = re.compile(r'[A-Za-z0-9_.@+/:~$#-]')
SKIP = re.compile(r'^docs/(0\.[^/]+|history)/')


def token_at(line, start, end):
    """The token around line[start:end]: identifier, path and URL characters, without trailing punctuation."""
    left = start
    while left > 0 and TOKEN_CHARS.match(line[left - 1]):
        left -= 1
    right = end
    while right < len(line) and TOKEN_CHARS.match(line[right]):
        right += 1
    return line[left:right].rstrip('.:,#').lstrip(':#')


def hits_in(path, text):
    for number, line in enumerate(text.split('\n'), 1):
        for match in NAME.finditer(line):
            yield path, number, token_at(line, match.start(), match.end()), line.strip()


def parse_allowlist(text):
    entries = []
    for number, raw in enumerate(text.split('\n'), 1):
        if not raw.strip() or raw.lstrip().startswith('#'):
            continue
        fields = raw.split('\t')
        fields = [f for f in fields if f != '']
        if len(fields) != 4:
            raise SystemExit(f'{ALLOWLIST}:{number}: expected 4 tab-separated fields (path, token, removal, reason), got {len(fields)}')
        path, token, removal, reason = fields
        entries.append({'line': number, 'path': re.compile(path), 'token': re.compile(token),
                        'removal': removal, 'reason': reason, 'used': 0})
    return entries


def allowed(entries, path, token):
    for entry in entries:
        if entry['path'].fullmatch(path) and entry['token'].fullmatch(token):
            entry['used'] += 1
            return True
    return False


def tracked_files():
    out = subprocess.run(['git', 'ls-files', '-z'], capture_output=True, check=True).stdout.decode('utf-8')
    return [p for p in out.split('\0') if p and not SKIP.match(p)]


def main():
    entries = parse_allowlist(open(ALLOWLIST, encoding='utf-8').read())
    bad = []
    for path in tracked_files():
        if path == ALLOWLIST:
            continue
        try:
            text = open(path, encoding='utf-8').read()
        except (UnicodeDecodeError, FileNotFoundError, IsADirectoryError):
            continue  # binary files, and submodule or deleted paths
        for hit in hits_in(path, text):
            if not allowed(entries, hit[0], hit[2]):
                bad.append(hit)
    for path, number, token, line in bad:
        print(f'::error file={path},line={number}::Leftover name "{token}" (FR-329): rename it, or allow it in {ALLOWLIST} with a reason and removal version. {line[:160]}')
    unused = [e for e in entries if e['used'] == 0]
    for entry in unused:
        print(f'::error file={ALLOWLIST},line={entry["line"]}::Allowlist entry matches nothing any more: remove it (FR-329).')
    if bad or unused:
        print(f'{len(bad)} leftover aide name(s), {len(unused)} unused allowlist entr(ies) (FR-329).', file=sys.stderr)
        return 1
    print(f'No leftover aide names: every hit is allowed by one of {len(entries)} entries in {ALLOWLIST} (FR-329)')
    return 0


def self_test():
    failures = 0

    def check(name, got, want):
        nonlocal failures
        if got == want:
            print(f'ok    {name}')
        else:
            print(f'FAIL  {name}: got {got!r}, wanted {want!r}')
            failures += 1

    tokens = lambda text: [h[2] for h in hits_in('x', text)]
    check('identifiers and paths match', tokens('aide_merges AideDbContext X-AIDE-Event .aide/rules.md'),
          ['aide_merges', 'AideDbContext', 'X-AIDE-Event', '.aide/rules.md'])
    check('English words don\'t', tokens('aided braided aides Aides'), [])
    check('camelCase parts and the plural match', tokens('asAide useAIDEx AIDEs'), ['asAide', 'AIDEs'])
    check('trailing punctuation is dropped', tokens('see aide-plan. and `AIDE_RUN_MIGRATIONS`,'), ['aide-plan', 'AIDE_RUN_MIGRATIONS'])
    check('urls keep their host', tokens('https://aide.benfielder.com/x'), ['https://aide.benfielder.com/x'])
    entries = parse_allowlist('# c\nbackend/.*\taide_merges\t0.9.0\tread old\n.*\tAIDE_RUN_MIGRATIONS\tnever\tgenerated projects\n')
    check('allowed by path and token', allowed(entries, 'backend/a.cs', 'aide_merges'), True)
    check('token allowed elsewhere only', allowed(entries, 'frontend/a.ts', 'aide_merges'), False)
    check('a token must match in full', allowed(entries, 'backend/a.cs', 'aide_merges_x'), False)
    check('frozen docs are skipped', bool(SKIP.match('docs/0.7.0/SPEC.md')) and bool(SKIP.match('docs/history/0.6.0.md')), True)
    check('live docs are scanned', bool(SKIP.match('docs/guides/a.md')), False)
    try:
        parse_allowlist('only\ttwo\n')
        check('a malformed entry fails', False, True)
    except SystemExit:
        check('a malformed entry fails', True, True)
    if failures:
        print(f'{failures} check-aide-names self-test(s) failed')
        return 1
    print('check-aide-names: all self-tests passed')
    return 0


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.exit(self_test() if '--self-test' in sys.argv else main())
