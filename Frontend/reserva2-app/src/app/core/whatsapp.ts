// Número de WhatsApp de soporte/ventas de Reserva2 (no confundir con el bot de WhatsApp
// Cloud API del plan Premium, que es para que cada comercio le hable a SUS clientes).
export const WHATSAPP_SOPORTE_NUMERO = '5492353410084';

export function linkWhatsApp(mensaje: string, numero: string = WHATSAPP_SOPORTE_NUMERO): string {
  return `https://wa.me/${numero}?text=${encodeURIComponent(mensaje)}`;
}

// ================= AVISO DE RESERVA AL COMERCIO =================
// Mensaje que el cliente le manda al comercio (Premium) desde la confirmación de la reserva.
export interface DatosMensajeReserva {
  comercio: string;
  cliente: string;
  whatsapp: string;
  fecha: string;       // "Martes 21 de octubre"
  hora: string;        // "15:30"
  servicio: string;
  profesional: string; // vacío si no eligió o el comercio no tiene profesionales
  local: string;       // vacío si el comercio tiene un solo local
  sena: string;        // vacío si no hay seña; si hay, una frase con el monto y cómo se pagó
}

// Datos de ejemplo para la vista previa del panel.
export const EJEMPLO_DATOS_MENSAJE_RESERVA: Omit<DatosMensajeReserva, 'comercio'> = {
  cliente: 'Juan Pérez',
  whatsapp: '341 555-1234',
  fecha: 'Martes 21 de octubre',
  hora: '15:30',
  servicio: 'Corte + barba',
  profesional: 'Naza',
  local: '',
  sena: 'Seña de $5.000 por transferencia (ya subí el comprobante)'
};

export function mensajeReserva(d: DatosMensajeReserva): string {
  const lineas = [
    `¡Hola! Reservé un turno en *${d.comercio}*`,
    `📅 ${d.fecha} a las ${d.hora} hs`,
    `📋 ${d.servicio}${d.profesional ? ' con ' + d.profesional : ''}`
  ];
  if (d.local) lineas.push(`📍 ${d.local}`);
  lineas.push(`👤 ${d.cliente} · ${d.whatsapp}`);
  if (d.sena) lineas.push(`💳 ${d.sena}`);
  lineas.push('', 'Reservado en reservados2.com');
  return lineas.join('\n');
}
