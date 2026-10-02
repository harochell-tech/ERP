import { describe, expect, it } from "vitest";
import { deliveryText, mailModeNotice, mailStatusLabel, mailStatusTone, parseAddresses, recipientsError, recipientsOf } from "@/lib/mail";

describe("mail", () => {
  it("names the states of a sending", () => {
    expect(["QUEUED", "SENT", "FAILED", "X"].map(mailStatusLabel)).toEqual(["En cola", "Enviado", "Fallido", "X"]);
    expect(["QUEUED", "SENT", "FAILED"].map(mailStatusTone)).toEqual(["PENDING_VERIFICATION", "ACTIVE", "REJECTED"]);
  });

  it("reads addresses typed in one box", () => {
    expect(parseAddresses(" Compras@Cliente.com.do; obra@cliente.com.do,\n compras@cliente.com.do ")).toEqual(["compras@cliente.com.do", "obra@cliente.com.do"]);
    expect(parseAddresses("  ")).toEqual([]);
  });

  it("joins the ticked saved e-mails with the typed ones, each once", () => {
    const saved = ["compras@cliente.com.do", "obra@cliente.com.do"];
    expect(recipientsOf(saved, new Set(), "")).toEqual(saved);
    expect(recipientsOf(saved, new Set(["obra@cliente.com.do"]), "gerencia@cliente.com.do, compras@cliente.com.do")).toEqual(["compras@cliente.com.do", "gerencia@cliente.com.do"]);
  });

  it("checks the recipients before sending", () => {
    expect(recipientsError([])).toBe("Marque o escriba al menos un correo.");
    expect(recipientsError(["a@b.do"])).toBeNull();
    expect(recipientsError(["a@b.do", "sin-arroba"])).toBe("Revise esta dirección: sin-arroba.");
    expect(recipientsError(["x", "y@"])).toBe("Revise estas direcciones: x, y@.");
    expect(recipientsError(Array.from({ length: 11 }, (_, i) => `c${i}@b.do`))).toBe("Un correo lleva como máximo 10 destinatarios.");
  });

  it("tells what the deployment does with mail", () => {
    expect(mailModeNotice("LIVE")).toBeNull();
    expect(mailModeNotice(null)).toBeNull();
    expect(mailModeNotice("OFF")).toBe("El envío de correos no está activado en este ambiente.");
    expect(mailModeNotice("REDIRECT")).toContain("no llega al cliente");
  });

  it("says where a sending ended up", () => {
    expect(deliveryText({ status: "QUEUED", deliveryMode: null, deliveredTo: null, attempts: 0, lastError: null })).toBe("Saldrá en unos segundos");
    expect(deliveryText({ status: "QUEUED", deliveryMode: null, deliveredTo: null, attempts: 2, lastError: "421 Try again later" })).toBe("Reintentando (2 intentos): 421 Try again later");
    expect(deliveryText({ status: "SENT", deliveryMode: "LIVE", deliveredTo: ["a@b.do"], attempts: 1, lastError: null })).toBe("Entregado al servidor de correo");
    expect(deliveryText({ status: "SENT", deliveryMode: "REDIRECT", deliveredTo: ["industrias@rochell.com.do"], attempts: 1, lastError: null })).toBe(
      "Redirigido a industrias@rochell.com.do (no llegó al cliente)",
    );
    expect(deliveryText({ status: "FAILED", deliveryMode: null, deliveredTo: null, attempts: 5, lastError: "550 rechazado" })).toBe("No se pudo enviar tras 5 intentos: 550 rechazado");
  });
});
