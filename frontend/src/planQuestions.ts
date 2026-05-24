export type PlanQuestion = {
  id: string;
  text: string;
  options: string[];
};

const fallbackOptions = [
  'Use the repository convention',
  'Let AI choose the safest option',
  'Ask for a narrower plan'
];

export function extractPlanQuestions(plan: string): PlanQuestion[] {
  const lines = plan.split(/\r?\n/);
  const start = lines.findIndex((line) => /blocking questions before implementation/i.test(line));
  if (start < 0) return [];

  const questions: string[] = [];
  let current = '';
  for (const line of lines.slice(start + 1)) {
    if (isNextSection(line) && current) break;

    const numbered = line.match(/^\s*\d+[.)]\s+(.+)$/);
    if (numbered) {
      if (current.trim()) questions.push(current.trim());
      current = numbered[1].trim();
      continue;
    }

    if (current && line.trim()) {
      current = `${current} ${line.trim()}`.trim();
    }
  }

  if (current.trim()) questions.push(current.trim());
  return questions.map((text, index) => ({
    id: `q-${index + 1}`,
    text,
    options: inferQuestionOptions(text)
  }));
}

export function formatPlanQuestionAnswers(questions: PlanQuestion[], answers: Record<string, string>): string {
  const lines = questions
    .map((question, index) => `${index + 1}. ${question.text}\nAnswer: ${answers[question.id]?.trim() ?? ''}`)
    .filter((entry) => !entry.endsWith('Answer: '));

  return `Answers to blocking questions before implementation:\n\n${lines.join('\n\n')}`;
}

function isNextSection(line: string) {
  const trimmed = line.trim();
  return !!trimmed &&
    !/^\d+[.)]\s+/.test(trimmed) &&
    /^(planned|plan|repository changes|tests|implementation|next steps|scope)\b/i.test(trimmed.replace(/\*\*/g, ''));
}

function inferQuestionOptions(question: string): string[] {
  const candidates = [
    ...extractRelevantCodeTokens(question),
    ...extractForExample(question),
    ...extractCommaOrOptions(question)
  ]
    .map(cleanOption)
    .filter((option) => isUsefulOption(option));

  const unique = candidates.filter((option, index) => candidates.findIndex((entry) => entry.toLowerCase() === option.toLowerCase()) === index);
  return [...unique, ...fallbackOptions]
    .filter((option, index, all) => all.findIndex((entry) => entry.toLowerCase() === option.toLowerCase()) === index)
    .slice(0, 3);
}

function extractRelevantCodeTokens(question: string) {
  const tokens = Array.from(question.matchAll(/`([^`]+)`/g)).map((match) => ({ value: match[1], index: match.index ?? 0 }));
  const viaIndex = question.search(/\bvia\b/i);
  if (viaIndex >= 0) {
    return tokens.filter((token) => token.index > viaIndex).map((token) => token.value);
  }

  return tokens.map((token) => token.value);
}

function extractForExample(question: string) {
  const match = question.match(/\bfor example\s+`([^`]+)`/i) ?? question.match(/\bfor example\s+([^?.,;]+)/i);
  return match ? [match[1]] : [];
}

function extractCommaOrOptions(question: string) {
  const normalized = question.replace(/`([^`]+)`/g, '$1');
  const afterShould = normalized.match(/\bshould\b.+?\b(?:use|be|host|route|expose|choose)\s+(.+)\?/i)?.[1] ?? '';
  const source = afterShould || normalized;
  return source
    .replace(/\banother\s+/ig, '')
    .split(/\s*,\s*|\s+\bor\b\s+/i)
    .map((part) => part.replace(/^(existing|a|an|the)\s+/i, '').trim())
    .filter((part) => part.length > 0);
}

function cleanOption(option: string) {
  const normalized = option
    .replace(/`/g, '')
    .replace(/^for example\s+/i, '')
    .replace(/^or\s+/i, '')
    .replace(/\s+/g, ' ')
    .replace(/^[,;:.]+/g, '')
    .trim();
  return normalized.endsWith('...')
    ? normalized
    : normalized.replace(/[,;:.]+$/g, '').trim();
}

function isUsefulOption(option: string) {
  if (!option || option.length > 90 || option.endsWith('?')) return false;
  if (/^(this|that|it|should|which|what|where|when|who|why|how)$/i.test(option)) return false;
  if (/^(use|be|host|route|expose|choose)$/i.test(option)) return false;
  if (/^exposed?\s+via\b/i.test(option)) return false;
  return true;
}
