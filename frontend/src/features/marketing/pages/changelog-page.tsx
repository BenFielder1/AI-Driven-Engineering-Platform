import { ChangelogEntryCard } from '../components/changelog-entry'
import { PageIntro } from '../components/page-intro'
import { changelog } from '../content/changelog'
import { changelogNote, pageTitles } from '../content/headings'
import { marketingPages } from '../content/pages'

const page = marketingPages['/changelog']

/** /changelog (FR-219): every version, newest first. */
export function ChangelogPage() {
  return (
    <>
      <PageIntro eyebrow={page.name} title={pageTitles.changelog}>
        {page.description} {changelogNote}
      </PageIntro>
      <div className="mx-auto flex max-w-3xl flex-col gap-6 px-4 pb-20">
        {changelog.map((entry) => (
          <ChangelogEntryCard key={entry.version} entry={entry} />
        ))}
      </div>
    </>
  )
}
