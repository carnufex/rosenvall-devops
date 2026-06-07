export type HighlightToken = {
  content: string;
  color?: string;
};

export type HighlightedLine = HighlightToken[];

const theme = 'github-dark';
const languageLoaders: Record<string, () => Promise<{ default: unknown }>> = {
  typescript: () => import('@shikijs/langs/typescript'),
  tsx: () => import('@shikijs/langs/tsx'),
  javascript: () => import('@shikijs/langs/javascript'),
  jsx: () => import('@shikijs/langs/jsx'),
  json: () => import('@shikijs/langs/json'),
  css: () => import('@shikijs/langs/css'),
  html: () => import('@shikijs/langs/html'),
  markdown: () => import('@shikijs/langs/markdown'),
  yaml: () => import('@shikijs/langs/yaml'),
  dockerfile: () => import('@shikijs/langs/dockerfile'),
  shellscript: () => import('@shikijs/langs/shellscript'),
  powershell: () => import('@shikijs/langs/powershell'),
  csharp: () => import('@shikijs/langs/csharp')
};

type ShikiHighlighter = {
  codeToTokens: (code: string, options: { lang: string; theme: string }) => { tokens: Array<Array<{ content: string; color?: string }>> };
  loadLanguage: (...languages: unknown[]) => Promise<void>;
  getLoadedLanguages: () => string[];
};

let highlighterPromise: Promise<ShikiHighlighter> | null = null;
const languagePromises = new Map<string, Promise<void>>();
const languageDependencies: Record<string, string[]> = {
  tsx: ['typescript', 'tsx'],
  jsx: ['javascript', 'jsx']
};

export function languageForPath(path: string | null | undefined): string {
  const normalized = (path ?? '').trim().toLowerCase();
  const name = normalized.split('/').pop() ?? normalized;
  if (!name) return 'plaintext';
  if (name === 'dockerfile' || name.endsWith('.dockerfile')) return 'dockerfile';
  if (name.endsWith('.tsx')) return 'tsx';
  if (name.endsWith('.ts') || name.endsWith('.mts') || name.endsWith('.cts')) return 'typescript';
  if (name.endsWith('.jsx')) return 'jsx';
  if (name.endsWith('.js') || name.endsWith('.mjs') || name.endsWith('.cjs')) return 'javascript';
  if (name.endsWith('.json') || name.endsWith('.jsonc')) return 'json';
  if (name.endsWith('.css')) return 'css';
  if (name.endsWith('.html') || name.endsWith('.htm')) return 'html';
  if (name.endsWith('.md') || name.endsWith('.mdx')) return 'markdown';
  if (name.endsWith('.yaml') || name.endsWith('.yml')) return 'yaml';
  if (name.endsWith('.sh') || name.endsWith('.bash') || name.endsWith('.zsh')) return 'shellscript';
  if (name.endsWith('.ps1') || name.endsWith('.psm1') || name.endsWith('.psd1')) return 'powershell';
  if (name.endsWith('.cs')) return 'csharp';
  return 'plaintext';
}

export function languagesForHighlightPath(path: string | null | undefined): string[] {
  return languagesForHighlightLanguage(languageForPath(path));
}

export function splitCodeLines(content: string): string[] {
  return content.replace(/\r\n/g, '\n').split('\n');
}

export function splitDiffLineForHighlight(text: string, kind: string): { prefix: string; code: string; highlightable: boolean } {
  if (kind === 'add' && text.startsWith('+')) {
    return { prefix: '+', code: text.slice(1), highlightable: true };
  }

  if (kind === 'delete' && text.startsWith('-')) {
    return { prefix: '-', code: text.slice(1), highlightable: true };
  }

  if (kind === 'context') {
    return text.startsWith(' ')
      ? { prefix: ' ', code: text.slice(1), highlightable: true }
      : { prefix: ' ', code: text, highlightable: true };
  }

  return { prefix: '', code: text, highlightable: false };
}

export function plainHighlightedLines(content: string): HighlightedLine[] {
  return splitCodeLines(content).map((line) => [{ content: line || ' ' }]);
}

export async function highlightCodeLines(content: string, path: string | null | undefined): Promise<HighlightedLine[]> {
  const language = languageForPath(path);
  if (language === 'plaintext') {
    return plainHighlightedLines(content);
  }

  try {
    const highlighter = await getHighlighter();
    await ensureLanguageLoaded(highlighter, language);
    const result = highlighter.codeToTokens(content, { lang: language, theme });
    return normalizeTokenLines(result.tokens, splitCodeLines(content));
  } catch {
    return plainHighlightedLines(content);
  }
}

function normalizeTokenLines(tokens: HighlightedLine[], sourceLines: string[]): HighlightedLine[] {
  return sourceLines.map((line, index) => {
    const tokenLine = tokens[index];
    if (!tokenLine || tokenLine.length === 0) {
      return [{ content: line || ' ' }];
    }

    return tokenLine.map((token) => ({
      content: token.content || ' ',
      color: token.color
    }));
  });
}

async function getHighlighter(): Promise<ShikiHighlighter> {
  if (!highlighterPromise) {
    highlighterPromise = Promise.all([
      import('shiki/core'),
      import('shiki/engine/javascript'),
      import('@shikijs/themes/github-dark')
    ]).then(async ([core, engine, themeModule]) => {
      const highlighter = await core.createHighlighterCore({
        themes: [themeModule.default],
        langs: [],
        engine: engine.createJavaScriptRegexEngine()
      });
      return highlighter as unknown as ShikiHighlighter;
    });
  }
  return highlighterPromise;
}

async function ensureLanguageLoaded(highlighter: ShikiHighlighter, language: string): Promise<void> {
  const languages = languagesForHighlightLanguage(language);
  for (const entry of languages) {
    if (highlighter.getLoadedLanguages().includes(entry)) {
      continue;
    }

    let loadPromise = languagePromises.get(entry);
    if (!loadPromise) {
      const loader = languageLoaders[entry];
      if (!loader) {
        return;
      }

      loadPromise = loader().then((module) => {
        const definitions = Array.isArray(module.default) ? module.default : [module.default];
        return highlighter.loadLanguage(...definitions);
      });
      languagePromises.set(entry, loadPromise);
    }

    await loadPromise;
  }
}

function languagesForHighlightLanguage(language: string): string[] {
  return language === 'plaintext' ? [] : languageDependencies[language] ?? [language];
}
