import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { Api } from '../../core/api';
import { Session } from '../../core/session';
import { PlanSelector } from '../../shared/plan-selector/plan-selector';

// Chocan con rutas del frontend o con /api y /uploads del servidor.
const ALIAS_RESERVADOS = ['panel', 'super-admin', 'terminos', 'privacidad', 'cancelar-turno', 'api', 'uploads'];

@Component({
  selector: 'app-panel-login',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, PlanSelector],
  templateUrl: './panel-login.html',
  styleUrls: ['./panel-login.css']
})
export class PanelLogin {
  modoRegistro = signal(false);
  errorAuth = signal<string | null>(null);
  cargandoAuth = signal(false);

  loginEmail = '';
  loginPassword = '';

  regNombre = '';
  regAliasUrl = '';
  regTipoPlantilla = 'Peluquería';
  regTelefono = '';
  regDatosBancarios = '';
  regEmail = '';
  regPassword = '';
  regPlan = 'Gratuito';
  regCiclo = 'Mensual';
  regCantidadProfesionales = 1;
  regCantidadSucursales = 1;

  precioCalculado = signal<number | null>(null);
  private precioRequestId = 0;

  constructor(private api: Api, private session: Session, private router: Router) {
    if (this.session.estaLogueado()) this.router.navigateByUrl('/panel');
    this.actualizarPrecio();
  }

  // Sucursales > 1 solo tiene sentido en Premium (Gratuito y Básico tienen tope de 1). Al
  // cambiar a un plan que no la permite, se resetea para no dejar cargado un valor inválido.
  onPlanOCicloCambio(): void {
    if (this.regPlan !== 'Premium') this.regCantidadSucursales = 1;
    this.actualizarPrecio();
  }

  actualizarPrecio(): void {
    if (this.regCantidadProfesionales < 1) this.regCantidadProfesionales = 1;
    if (this.regCantidadSucursales < 1) this.regCantidadSucursales = 1;

    // Descarta respuestas que lleguen desordenadas (ej. el usuario cambia varios campos
    // rápido y una request vieja tarda más que una nueva): solo se aplica la última pedida.
    const idPedido = ++this.precioRequestId;
    this.api.getPrecioPlan(this.regPlan, this.regCiclo, this.regCantidadProfesionales, this.regCantidadSucursales).subscribe({
      next: r => { if (idPedido === this.precioRequestId) this.precioCalculado.set(r.precio); },
      error: () => { if (idPedido === this.precioRequestId) this.precioCalculado.set(null); }
    });
  }

  precioTexto(): string {
    const precio = this.precioCalculado();
    if (precio === null) return '...';
    if (precio <= 0) return 'Gratis';
    const periodo = this.regCiclo === 'Anual' ? '/año' : '/mes';
    return `$${precio.toLocaleString('es-AR')}${periodo}`;
  }

  login(): void {
    this.errorAuth.set(null);
    this.cargandoAuth.set(true);
    this.api.login(this.loginEmail.trim(), this.loginPassword).subscribe({
      next: resp => {
        this.session.iniciarSesion(resp);
        this.cargandoAuth.set(false);
        this.router.navigateByUrl('/panel');
      },
      error: () => {
        this.cargandoAuth.set(false);
        this.errorAuth.set('Email o contraseña incorrectos.');
      }
    });
  }

  // Mismas reglas que valida el backend al registrar.
  private errorRegistro(alias: string): string | null {
    if (!this.regNombre.trim()) return 'Ingresá el nombre del negocio.';
    if (!/^[a-z0-9-]{3,40}$/.test(alias)) return 'El link tiene que tener entre 3 y 40 caracteres: solo minúsculas, números y guiones.';
    if (ALIAS_RESERVADOS.includes(alias)) return 'Ese link está reservado. Elegí otro.';
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(this.regEmail.trim())) return 'Ingresá un email válido.';
    if (this.regPassword.length < 8) return 'La contraseña tiene que tener al menos 8 caracteres.';
    return null;
  }

  registrar(): void {
    const alias = this.regAliasUrl.trim().toLowerCase();
    this.regAliasUrl = alias;
    const error = this.errorRegistro(alias);
    this.errorAuth.set(error);
    if (error) return;

    this.cargandoAuth.set(true);
    this.api.registrar({
      nombre: this.regNombre.trim(),
      aliasUrl: alias,
      tipoPlantilla: this.regTipoPlantilla,
      telefonoNotificaciones: this.regTelefono.trim(),
      datosBancarios: this.regDatosBancarios.trim(),
      email: this.regEmail.trim(),
      password: this.regPassword,
      planActual: this.regPlan,
      cicloFacturacion: this.regCiclo,
      cantidadProfesionales: this.regCantidadProfesionales,
      cantidadSucursales: this.regCantidadSucursales
    }).subscribe({
      next: resp => {
        this.session.iniciarSesion(resp);
        this.cargandoAuth.set(false);
        this.router.navigateByUrl('/panel');
      },
      error: err => {
        this.cargandoAuth.set(false);
        this.errorAuth.set(
          err.status === 409 ? err.error?.mensaje ?? 'Ya existe una cuenta con esos datos.'
          : err.status === 400 && err.error?.mensaje ? err.error.mensaje
          : 'No pudimos crear la cuenta.');
      }
    });
  }
}
