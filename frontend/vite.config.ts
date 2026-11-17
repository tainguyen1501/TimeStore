import path from "node:path"
import { tanstackRouter } from "@tanstack/router-plugin/vite"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig, loadEnv } from "vite"

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, __dirname, "")
  // The admin port of TimeStore.Server (TimeStore:AdminPort).
  const apiTarget = env.VITE_API_PROXY_TARGET || "http://localhost:9001"

  return {
    // The router plugin must run before the React plugin.
    plugins: [
      tanstackRouter({ target: "react", autoCodeSplitting: true }),
      react(),
      tailwindcss(),
    ],
    resolve: {
      alias: { "@": path.resolve(__dirname, "./src") },
    },
    server: {
      port: Number(env.VITE_PORT || "5173"),
      // Dev only: the Server serves the UI on the admin port in production, so no CORS is needed.
      proxy: {
        "/api": { target: apiTarget, changeOrigin: true },
      },
    },
    build: {
      // Embedded into the Server binary by its build (TimeStore.Server.csproj) when present.
      outDir: "dist",
    },
  }
})
