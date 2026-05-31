import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  server: {
    host: "0.0.0.0",
    port: 17051,
    proxy: {
      "/api": {
        target: "http://localhost:17052",
        changeOrigin: true
      },
      "/health": {
        target: "http://localhost:17052",
        changeOrigin: true
      }
    }
  }
});
