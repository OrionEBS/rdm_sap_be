import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// Dev: gọi API qua proxy (không vướng CORS) — VITE_API_PROXY mặc định http://localhost:5200.
// Build: base './' để đặt bản build ở bất kỳ thư mục / site IIS nào; API lấy từ VITE_API_BASE_URL (xem .env.example).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '');
  const target = env.VITE_API_PROXY || 'http://localhost:5200';
  return {
    base: './',
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': { target, changeOrigin: true, secure: false },
        '/health': { target, changeOrigin: true, secure: false },
        '/swagger': { target, changeOrigin: true, secure: false }
      }
    },
    build: { outDir: 'dist', sourcemap: false }
  };
});
