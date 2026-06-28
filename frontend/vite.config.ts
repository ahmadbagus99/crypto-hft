import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      "/api": "http://api:8080",
      "/hubs": {
        target: "http://api:8080",
        ws: true
      }
    }
  }
});
