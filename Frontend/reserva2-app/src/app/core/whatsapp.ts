// Número de WhatsApp de soporte/ventas de Reserva2 (no confundir con el bot de WhatsApp
// Cloud API del plan Premium, que es para que cada comercio le hable a SUS clientes).
export const WHATSAPP_SOPORTE_NUMERO = '5492353410084';

export function linkWhatsApp(mensaje: string, numero: string = WHATSAPP_SOPORTE_NUMERO): string {
  return `https://wa.me/${numero}?text=${encodeURIComponent(mensaje)}`;
}

// ================= AVISO DE RESERVA AL COMERCIO =================
// Mensaje que el cliente le manda al comercio desde la confirmación de la reserva. El
// comercio puede escribir el suyo en Perfil usando estas variables; si no, va el de abajo.
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

export const VARIABLES_MENSAJE_RESERVA: { clave: keyof DatosMensajeReserva; descripcion: string }[] = [
  { clave: 'cliente', descripcion: 'Nombre del cliente' },
  { clave: 'whatsapp', descripcion: 'WhatsApp del cliente' },
  { clave: 'fecha', descripcion: 'Día del turno' },
  { clave: 'hora', descripcion: 'Hora del turno' },
  { clave: 'servicio', descripcion: 'Servicio' },
  { clave: 'profesional', descripcion: 'Profesional' },
  { clave: 'local', descripcion: 'Local' },
  { clave: 'sena', descripcion: 'Seña (monto y cómo se pagó)' },
  { clave: 'comercio', descripcion: 'Nombre de tu negocio' }
];

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

export function mensajeReservaPorDefecto(d: DatosMensajeReserva): string {
  const lineas = [
    `¡Hola! Reservé un turno en *${d.comercio}*`,
    `📅 ${d.fecha} a las ${d.hora} hs`,
    `✂️ ${d.servicio}${d.profesional ? ' con ' + d.profesional : ''}`
  ];
  if (d.local) lineas.push(`📍 ${d.local}`);
  lineas.push(`👤 ${d.cliente} · ${d.whatsapp}`);
  if (d.sena) lineas.push(`💳 ${d.sena}`);
  lineas.push('', 'Reservado en reservados2.com');
  return lineas.join('\n');
}

// Reemplaza {cliente}, {fecha}, etc. (también acepta {seña} y mayúsculas). Una variable
// que no existe queda tal cual, para que el comercio vea que la escribió mal.
export function armarMensajeReserva(plantilla: string | null | undefined, d: DatosMensajeReserva): string {
  if (!plantilla?.trim()) return mensajeReservaPorDefecto(d);
  return plantilla.replace(/\{\s*([a-zA-ZñÑ]+)\s*\}/g, (original, nombre: string) => {
    const clave = nombre.toLowerCase().replace('ñ', 'n') as keyof DatosMensajeReserva;
    return clave in d ? d[clave] : original;
  });
}
