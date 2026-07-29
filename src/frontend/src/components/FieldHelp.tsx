'use client';

import { CLOUD_FIELD_HELP, type CloudHelpKey, helpCloudsHref } from '@/lib/cloud-help';

/** Compact tip + “Details” link under a form control. */
export function FieldHelp({ helpKey }: { helpKey: CloudHelpKey }) {
  const h = CLOUD_FIELD_HELP[helpKey];
  return (
    <p className="mt-1 text-xs text-gray-500 leading-snug">
      {h.tip}{' '}
      <a
        href={helpCloudsHref(h.helpId)}
        target="_blank"
        rel="noopener noreferrer"
        className="text-azure-600 hover:text-azure-700 underline whitespace-nowrap"
      >
        Details
      </a>
    </p>
  );
}

/** Section header link into /help/clouds. */
export function CloudHelpLink({
  section,
  label = 'How to find these values',
}: {
  section: string;
  label?: string;
}) {
  return (
    <a
      href={helpCloudsHref(section)}
      target="_blank"
      rel="noopener noreferrer"
      className="text-xs text-azure-600 hover:text-azure-700 underline"
    >
      {label}
    </a>
  );
}
