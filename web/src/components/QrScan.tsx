"use client";

import jsQR from "jsqr";
import { useEffect, useRef, useState } from "react";

// OCR1-03 (E-OCR1-03-6): the printed e-CF's QR, read in the browser — with the camera (phone or computer), from a photo of it, or by
// pasting its link. The image never leaves the browser; only the link the QR holds goes to the server.

/** The text of the first QR in the picture, or null. */
function decode(source: CanvasImageSource, width: number, height: number, canvas: HTMLCanvasElement): string | null {
  if (width === 0 || height === 0) {
    return null;
  }
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext("2d", { willReadFrequently: true });
  if (!context) {
    return null;
  }
  context.drawImage(source, 0, 0, width, height);
  const image = context.getImageData(0, 0, width, height);
  return jsQR(image.data, width, height)?.data ?? null;
}

function Camera({ onRead, onStop }: { onRead: (text: string) => void; onStop: (problem: string | null) => void }) {
  const video = useRef<HTMLVideoElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  // The callbacks change on every render of the parent; the camera opens once.
  const handlers = useRef({ onRead, onStop });
  useEffect(() => {
    handlers.current = { onRead, onStop };
  });
  useEffect(() => {
    let stream: MediaStream | null = null;
    let timer = 0;
    let stopped = false;
    navigator.mediaDevices
      ?.getUserMedia({ video: { facingMode: "environment" }, audio: false })
      .then((s) => {
        if (stopped) {
          s.getTracks().forEach((t) => t.stop());
          return;
        }
        stream = s;
        const v = video.current;
        if (!v) {
          return;
        }
        v.srcObject = s;
        void v.play();
        timer = window.setInterval(() => {
          if (v.readyState >= 2 && canvas.current) {
            const text = decode(v, v.videoWidth, v.videoHeight, canvas.current);
            if (text) {
              handlers.current.onRead(text);
            }
          }
        }, 300);
      })
      .catch(() => handlers.current.onStop("No se pudo abrir la cámara. Suba una foto del QR o pegue su enlace."));
    if (!navigator.mediaDevices) {
      window.setTimeout(() => handlers.current.onStop("Este navegador no da acceso a la cámara. Suba una foto del QR o pegue su enlace."), 0);
    }
    return () => {
      stopped = true;
      window.clearInterval(timer);
      stream?.getTracks().forEach((t) => t.stop());
    };
  }, []);
  return (
    <div className="qr-camera">
      <video ref={video} muted playsInline aria-label="Cámara para leer el QR" />
      <canvas ref={canvas} hidden />
    </div>
  );
}

/** «Escanear QR»: calls <paramref name="onLink"/> with the link the QR holds. */
export function QrScan({ onLink, busy }: { onLink: (link: string) => void; busy: boolean }) {
  const [camera, setCamera] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [pasted, setPasted] = useState("");
  const canvas = useRef<HTMLCanvasElement>(null);

  const read = (text: string) => {
    setCamera(false);
    setProblem(null);
    onLink(text);
  };

  const fromPhoto = async (file: File | undefined) => {
    if (!file || !canvas.current) {
      return;
    }
    try {
      const bitmap = await createImageBitmap(file);
      const text = decode(bitmap, bitmap.width, bitmap.height, canvas.current);
      bitmap.close();
      if (text) {
        read(text);
      } else {
        setProblem("No se encontró un QR en la foto. Tómela de cerca, con buena luz y el QR completo.");
      }
    } catch {
      setProblem("No se pudo leer la imagen.");
    }
  };

  return (
    <section className="qr-scan" data-testid="qr-scan">
      <div className="actions">
        <button type="button" disabled={busy} onClick={() => setCamera((c) => !c)}>
          {camera ? "Cerrar la cámara" : "Escanear QR con la cámara"}
        </button>
        <label className="button">
          Subir foto del QR
          <input type="file" accept="image/*" hidden disabled={busy} aria-label="Foto del QR" onChange={(e) => void fromPhoto(e.target.files?.[0])} />
        </label>
      </div>
      {camera ? (
        <Camera
          onRead={read}
          onStop={(text) => {
            setCamera(false);
            setProblem(text);
          }}
        />
      ) : null}
      <canvas ref={canvas} hidden />
      <form
        className="actions"
        onSubmit={(e) => {
          e.preventDefault();
          if (pasted.trim()) {
            read(pasted.trim());
            setPasted("");
          }
        }}
      >
        <input type="url" aria-label="Enlace del QR" placeholder="https://ecf.dgii.gov.do/…" value={pasted} onChange={(e) => setPasted(e.target.value)} />
        <button type="submit" disabled={busy || !pasted.trim()}>
          Usar enlace
        </button>
      </form>
      {problem ? (
        <p className="error" role="alert">
          {problem}
        </p>
      ) : null}
    </section>
  );
}
