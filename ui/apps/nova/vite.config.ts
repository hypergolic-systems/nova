import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { resolve } from 'path';

// Library-mode build emitting ESM modules into dist/, deployed by
// `just install` to GameData/Nova/UI/. Dragonglass's sidecar import
// map then resolves @nova/hud → /Nova/hud.js.
//
// All shared-runtime specifiers (svelte, three, threlte, every
// @dragonglass/* package) are externalized so the bundle never bakes
// in its own copy — the importmap routes them to Dragonglass's
// emitted runtime, giving Nova a single shared Svelte instance with
// stock and any other UI mod.
//
// Deterministic filenames mirror Dragonglass's runtime config: hashes
// would force a sidecar restart on every UI rebuild.
export default defineConfig({
  // emitCss=false makes svelte-plugin inject component styles via
  // runtime <style> tags rather than emitting a sibling .css file,
  // because Dragonglass deliberately doesn't auto-link mod CSS (see
  // mod-ui.md §CSS). When the real UI grows shared sheets, switch
  // to CSS modules + document.adoptedStyleSheets.
  plugins: [svelte({ emitCss: false })],
  build: {
    lib: {
      entry: { hud: resolve(__dirname, 'src/hud.ts') },
      formats: ['es'],
    },
    rollupOptions: {
      external: [
        'svelte',
        /^svelte\//,
        'three',
        /^three\//,
        '@threlte/core',
        /^@dragonglass\//,
      ],
      output: {
        entryFileNames: '[name].js',
        chunkFileNames: 'chunks/[name]-[hash].js',
        assetFileNames: '[name][extname]',
      },
    },
    minify: false,
    modulePreload: false,
    outDir: 'dist',
    emptyOutDir: true,
  },
});
