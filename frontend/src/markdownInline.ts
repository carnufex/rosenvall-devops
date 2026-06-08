import * as React from 'react';

function safeInlineMarkdownHref(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed || /[\u0000-\u001f\u007f]/.test(trimmed)) return null;
  if (trimmed.startsWith('/') && !trimmed.startsWith('//')) return trimmed;
  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol === 'https:') return parsed.toString();
    if (parsed.protocol === 'http:' && ['localhost', '127.0.0.1', '::1'].includes(parsed.hostname)) return parsed.toString();
  } catch {
    return null;
  }

  return null;
}

export function renderInlineMarkdown(text: string): React.ReactNode[] {
  const nodes: React.ReactNode[] = [];
  const pattern = /(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\))/g;
  let last = 0;
  for (const match of text.matchAll(pattern)) {
    if (match.index > last) {
      nodes.push(text.slice(last, match.index));
    }

    const token = match[0];
    if (token.startsWith('**')) {
      nodes.push(React.createElement('strong', { key: nodes.length }, token.slice(2, -2)));
    } else if (token.startsWith('`')) {
      nodes.push(React.createElement('code', { key: nodes.length }, token.slice(1, -1)));
    } else {
      const link = token.match(/^\[([^\]]+)\]\(([^)]+)\)$/);
      const href = link ? safeInlineMarkdownHref(link[2]) : null;
      nodes.push(link && href
        ? React.createElement('a', { href, key: nodes.length, rel: 'noreferrer', target: '_blank' }, link[1])
        : token);
    }

    last = match.index + token.length;
  }

  if (last < text.length) {
    nodes.push(text.slice(last));
  }

  return nodes;
}
