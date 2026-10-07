import type { Metadata, Viewport } from "next";
import type { ReactNode } from "react";
import { Shell } from "@/components/Shell";
import { SessionProvider } from "@/lib/session";
import { ToastProvider } from "@/lib/toast";
// E-UI-6: the typefaces ship with the export; no call to an external font service.
import "@fontsource/ibm-plex-sans/400.css";
import "@fontsource/ibm-plex-sans/500.css";
import "@fontsource/ibm-plex-sans/600.css";
import "@fontsource/ibm-plex-sans/700.css";
import "@fontsource/ibm-plex-mono/400.css";
import "@fontsource/ibm-plex-mono/500.css";
import "./globals.css";

export const metadata: Metadata = {
  title: "Rochell Core",
  description: "ERP/MES de Industrias Rochell",
  // UX6-01 (E-UX6-4): the iPhone's «Agregar a inicio» opens it full screen under its short name.
  appleWebApp: { capable: true, title: "Rochell", statusBarStyle: "default" },
};

export const viewport: Viewport = { themeColor: "#0b5cad", width: "device-width", initialScale: 1 };

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="es-DO">
      <body>
        <SessionProvider>
          <ToastProvider>
            <Shell>{children}</Shell>
          </ToastProvider>
        </SessionProvider>
      </body>
    </html>
  );
}
