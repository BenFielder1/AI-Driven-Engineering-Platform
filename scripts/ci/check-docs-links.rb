# frozen_string_literal: true

# FR-226: offline internal link check of the built docs site (docs/_site). External links are skipped; internal links,
# images, scripts and #anchors must resolve, or this exits non-zero and fails the Docs workflow.
#
# Usage (from docs/, after `bundle exec jekyll build`):  bundle exec ruby ../scripts/ci/check-docs-links.rb _site
#
# The site is served from the root of its custom domain (https://docs.tevrin.dev/, empty baseurl), so root-relative
# URLs resolve against the _site root as they are.
#
# Frozen docs/0.1.0-0.6.0 pages are not scanned as link *sources*: they predate the site and were written with repo-root
# paths (e.g. `docs/0.1.0/SPEC.md` from inside docs/0.2.0/), and frozen docs may only change to fix links. Links *to* them
# from new pages (history/) are still checked. Once their links are fixed, drop FROZEN.
require "html-proofer"

site = ARGV.fetch(0, "_site")
abort("check-docs-links: #{site} not found; build the site first") unless Dir.exist?(site)

# Anchored to the site root so history/<x.y.z>/ pages (which link into the frozen specs) are still checked.
FROZEN = %r{\A#{Regexp.escape(site.chomp("/"))}/0\.[1-6]\.0/}

options = {
  disable_external: true,
  enforce_https: false, # localhost URLs in dev-loop docs are fine; external links aren't checked anyway
  check_internal_hash: true,
  allow_hash_href: true,
  allow_missing_href: true, # theme buttons/anchors without href aren't links
  ignore_missing_alt: false,
  ignore_files: [FROZEN],
  log_level: :info,
}

# D-V6-26: the old vN URLs must still resolve, as redirect pages written by jekyll-redirect-from. The frozen pages get
# redirect_from only through _config.yml defaults, so a missing page here means the redirects silently stopped working.
redirects = (1..6).map { |n| "history/v#{n}/index.html" } + (1..5).map { |n| "v#{n}/SPEC/index.html" }
missing = redirects.reject { |path| File.exist?(File.join(site, path)) }
abort("check-docs-links: missing redirect pages for old URLs: #{missing.join(", ")}") unless missing.empty?

HTMLProofer.check_directory(site, options).run
