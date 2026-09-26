import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],

  // The shell loads the built assets from the Tauri bundle, so paths must be relative.
  base: './',

  build: {
    outDir: 'dist',
    emptyOutDir: true,
    // The Tauri shell is the only consumer; source maps make field debugging possible without a rebuild.
    sourcemap: true,
  },

  server: {
    port: 5173,
    strictPort: true,
  },

  test: {
    environment: 'happy-dom',
    include: ['tests/**/*.test.ts'],
    // Tests that touch a live API need real timers and real sockets.
    testTimeout: 20000,
  },
})
