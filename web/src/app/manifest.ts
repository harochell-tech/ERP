import type { MetadataRoute } from "next";

// UX6-01 (E-UX6-4): installed from the browser ("Agregar a la pantalla de inicio") the app opens full screen with its icon.
export const dynamic = "force-static";

export default function manifest(): MetadataRoute.Manifest {
  return {
    name: "Rochell Core",
    short_name: "Rochell",
    description: "ERP/MES de Industrias Rochell",
    lang: "es-DO",
    start_url: "/",
    scope: "/",
    display: "standalone",
    background_color: "#f4f3ef",
    theme_color: "#0b5cad",
    icons: [
      { src: "/icons/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/icons/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
      { src: "/icons/maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
    ],
  };
}
