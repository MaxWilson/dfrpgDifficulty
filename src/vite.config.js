import { defineConfig } from 'vite'

export default defineConfig({
  root: "ShiningSword/UI/",
  base: '/dfrpgDifficulty/',  
  build: {
    outDir: "publish",
    emptyOutDir: true,
    sourcemap: true
  }
});
