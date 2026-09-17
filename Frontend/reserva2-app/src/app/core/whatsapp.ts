// Número de WhatsApp de soporte/ventas de Reserva2 (no confundir con el bot de WhatsApp
// Cloud API del plan Premium, que es para que cada comercio le hable a SUS clientes).
export const WHATSAPP_SOPORTE_NUMERO = '5492353410084';

export function linkWhatsApp(mensaje: string, numero: string = WHATSAPP_SOPORTE_NUMERO): string {
  return `https://wa.me/${numero}?text=${encodeURIComponent(mensaje)}`;
}
