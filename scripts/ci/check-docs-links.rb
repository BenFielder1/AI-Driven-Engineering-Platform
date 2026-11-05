# frozen_string_literal: true

# FR-226: offline internal link check of the built docs site (docs/_site). External links are skipped; internal links,
# images, scripts and #anchors must resolve, or this exits non-zero and fails the Docs workflow.
#
# Usage (from docs/, after `bundle exec jekyll build`):  bundle exec ruby ../scripts/ci/check-docs-links.rb _site
#
# The site is built with baseurl /AI-Driven-Engineering-Platform, so root-relative URLs are swapped back to the
# _site root before resolving.
#
# Frozen docs/v1-v5 pages are not scanned as link *sources*: they predate the site and were written with repo-root
# paths (e.g. `docs/v1/SPEC.md` from inside docs/v2/), and frozen docs may only change to fix links. Links *to* them
# from new pages (history/) are still checked. Once their links are fixed, drop FROZEN.
require "html-proofer"

site = ARGV.fetch(0, "_site")
abort("check-docs-links: #{site} not found; build the site first") unless Dir.exist?(site)

# Anchored to the site root so history/vN/ pages (which link into the frozen specs) are still checked.
FROZEN = %r{\A#{Regexp.escape(site.chomp("/"))}/v[1-5]/}

options = {
  disable_external: true,
  enforce_https: false, # localhost URLs in dev-loop docs are fine; external links aren't checked anyway
  check_internal_hash: true,
  allow_hash_href: true,
  allow_missing_href: true, # theme buttons/anchors without href aren't links
  ignore_missing_alt: false,
  ignore_files: [FROZEN],
  swap_urls: { %r{\A/AI-Driven-Engineering-Platform(/|\z)} => "/" },
  log_level: :info,
}

HTMLProofer.check_directory(site, options).run
