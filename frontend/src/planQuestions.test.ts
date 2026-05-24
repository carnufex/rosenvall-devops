import test from 'node:test';
import assert from 'node:assert/strict';
import { extractPlanQuestions, formatPlanQuestionAnswers } from './planQuestions.ts';

test('extracts blocking questions and menu options from an AI plan', () => {
  const questions = extractPlanQuestions(`
Blocking questions before implementation:

1. Which cluster path should host this, for example \`clusters/<cluster-name>/...\`?
2. Should \`test.rosenvall.se\` be exposed via existing \`Ingress\`, \`HTTPRoute/Gateway API\`, or another repo-specific routing pattern?
3. Should the Hello World page use default nginx content, or a custom page with exact text such as \`Hello World\`?

Planned repository changes, PR-first only:
1. Inspect existing GitOps patterns.
`);

  assert.equal(questions.length, 3);
  assert.equal(questions[0].text.startsWith('Which cluster path'), true);
  assert.equal(questions[0].options[0], 'clusters/<cluster-name>/...');
  assert.ok(questions[1].options.includes('Ingress'));
  assert.ok(questions[1].options.includes('HTTPRoute/Gateway API'));
  assert.ok(questions[1].options.includes('repo-specific routing pattern'));
  assert.equal(questions[1].options.includes('test.rosenvall.se'), false);
  assert.ok(questions[2].options.includes('Hello World'));
});

test('formats answers as a comment that can be sent back to AI', () => {
  const questions = extractPlanQuestions(`
Blocking questions before implementation:
1. Which namespace?
2. Which hostname?
`);

  const comment = formatPlanQuestionAnswers(questions, {
    'q-1': 'test',
    'q-2': 'test.rosenvall.se'
  });

  assert.match(comment, /Answers to blocking questions/);
  assert.match(comment, /Answer: test/);
  assert.match(comment, /Answer: test\.rosenvall\.se/);
});
