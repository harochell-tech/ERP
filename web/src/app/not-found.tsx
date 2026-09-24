import Link from "next/link";

export default function NotFound() {
  return (
    <>
      <h1>Página no encontrada</h1>
      <Link href="/">Volver al inicio</Link>
    </>
  );
}
