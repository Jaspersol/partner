import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'

import { LIVE2D_BANNER } from './src/pet/licence'

/**
 * `VITE_PARTNER_PET=0` builds the shell alone: the Live2D runtime is behind a licence gate, and a build that
 * excludes it must not so much as emit `pet.html`.
 */
const petEnabled = process.env.VITE_PARTNER_PET !== '0'

/** True when a chunk's own modules come from easy-live2d, which is what bundles the Cubism Framework. */
function carriesLive2d(moduleIds: readonly string[] | undefined): boolean {
  return (moduleIds ?? []).some((id) => id.includes('easy-live2d'))
}

export default defineConfig({
  plugins: [vue()],

  // The shell loads the built assets from the Tauri bundle, so paths must be relative.
  base: './',

  build: {
    outDir: 'dist',
    emptyOutDir: true,
    // The Tauri shell is the only consumer; source maps make field debugging possible without a rebuild.
    sourcemap: true,

    rollupOptions: {
      input: petEnabled ? { main: 'index.html', pet: 'pet.html' } : { main: 'index.html' },
      output: {
        /**
         * The bundled Cubism Framework's copyright headers are `/*!` comments in Live2D's own sources, but esbuild
         * drops them when it bundles, and the Open Software License does not allow that notice to be removed. The
         * banner therefore follows the framework wherever Rollup puts it: the pet entry, and any chunk that carries
         * easy-live2d itself (`PetApp` imports it dynamically, so it is split out).
         */
        banner: (chunk) =>
          petEnabled && (chunk.name === 'pet' || carriesLive2d(chunk.moduleIds)) ? LIVE2D_BANNER : '',
      },
    },
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