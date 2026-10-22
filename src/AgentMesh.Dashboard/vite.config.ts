import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: parseInt(process.env.PORT || '3000'),
    strictPort: true,
    host: true,
    proxy: {
      '/dex': {
        target: 'http://localhost:8085',
        changeOrigin: true
      },
      '/api': {
        target: process.env.services__api__http__0 || 'http://localhost:5000',
        changeOrigin: true
      },
      '/hubs': {
        target: process.env.services__api__http__0 || 'http://localhost:5000',
        ws: true
      }
    }
  }
});
