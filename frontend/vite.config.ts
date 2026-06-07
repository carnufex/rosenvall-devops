import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { execSync } from 'node:child_process';

process.env.VITE_RELEASE_COMMIT_SHA ??= gitValue('git rev-parse HEAD');
process.env.VITE_RELEASE_BUILD_TIMESTAMP ??= new Date().toISOString();

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5088',
      '/hubs': {
        target: 'http://localhost:5088',
        ws: true
      },
      '/authentik': {
        target: 'https://authentik.rosenvall.se',
        changeOrigin: true,
        secure: true,
        rewrite: (path) => path.replace(/^\/authentik/, '')
      }
    }
  }
});

function gitValue(command: string) {
  try {
    return execSync(command, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim() || 'unknown';
  } catch {
    return 'unknown';
  }
}
