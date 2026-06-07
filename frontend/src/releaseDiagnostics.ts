export type FrontendReleaseDiagnostics = {
  commitSha: string;
  buildTimestamp: string;
};

export function normalizeFrontendReleaseDiagnostics(env: Record<string, string | undefined>): FrontendReleaseDiagnostics {
  return {
    commitSha: releaseValue(env.VITE_RELEASE_COMMIT_SHA),
    buildTimestamp: releaseValue(env.VITE_RELEASE_BUILD_TIMESTAMP)
  };
}

const viteEnv = (import.meta as unknown as { env?: Record<string, string | undefined> }).env ?? {};

export const frontendReleaseDiagnostics = normalizeFrontendReleaseDiagnostics(viteEnv);

function releaseValue(value: string | undefined) {
  const normalized = value?.trim();
  return normalized ? normalized : 'unknown';
}
