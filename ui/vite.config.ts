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

    // The API is same-origin inside the Tauri webview, so it sends no CORS headers and needs none. Opening
    // the dev server in a normal browser is the other way to look at this UI, and there the two are on
    // different ports; the proxy makes the dev server the origin so the app code is identical either way.
    proxy: {
      '/api': {
        target: `http://127.0.0.1:${process.env.PARTNER_PORT ?? '5280'}`,
        changeOrigin: false,
      },
    },
  },

  test: {
    environment: 'happy-dom',
    include: ['tests/**/*.test.ts'],
    // Tests that touch a live API need real timers and real sockets.
    testTimeout: 20000,
  },
})
