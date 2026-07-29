import { HELP_SECTIONS } from '@/lib/cloud-help';

export default function CloudsHelpPage() {
  return (
    <div className="max-w-3xl mx-auto space-y-8 pb-12">
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Adding clouds</h1>
        <p className="text-sm text-gray-500 mt-1">
          Essential values for Azure, AWS, and GCP. Opened from field tips on the Clouds page.
        </p>
      </div>

      <nav className="flex flex-wrap gap-2 text-sm">
        <a href="#azure" className="px-3 py-1 rounded-full bg-blue-50 text-blue-700 border border-blue-100">Azure</a>
        <a href="#aws-org" className="px-3 py-1 rounded-full bg-amber-50 text-amber-800 border border-amber-100">AWS</a>
        <a href="#gcp-org" className="px-3 py-1 rounded-full bg-emerald-50 text-emerald-800 border border-emerald-100">GCP</a>
      </nav>

      {HELP_SECTIONS.map((section) => (
        <section
          key={section.id}
          id={section.id}
          className="scroll-mt-20 bg-white rounded-lg border border-gray-200 p-5"
        >
          <h2 className="text-base font-semibold text-gray-900 mb-2">{section.title}</h2>
          <ul className="list-disc pl-5 space-y-1.5 text-sm text-gray-700">
            {section.bullets.map((b) => (
              <li key={b}>{b}</li>
            ))}
          </ul>
          {'links' in section && section.links && section.links.length > 0 && (
            <ul className="mt-3 space-y-1 text-sm">
              {section.links.map((l) => (
                <li key={l.href}>
                  <a
                    href={l.href}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-azure-600 hover:text-azure-700 underline"
                  >
                    {l.label} ↗
                  </a>
                </li>
              ))}
            </ul>
          )}
        </section>
      ))}
    </div>
  );
}
