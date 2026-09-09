import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { Api } from '../../core/api';
import { Session } from '../../core/session';

@Component({
  selector: 'app-panel-login',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
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

  constructor(private api: Api, private session: Session, private router: Router) {
    if (this.session.estaLogueado()) this.router.navigateByUrl('/panel');
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

  registrar(): void {
    this.errorAuth.set(null);
    this.cargandoAuth.set(true);
    this.api.registrar({
      nombre: this.regNombre.trim(),
      aliasUrl: this.regAliasUrl.trim(),
      tipoPlantilla: this.regTipoPlantilla,
      telefonoNotificaciones: this.regTelefono.trim(),
      datosBancarios: this.regDatosBancarios.trim(),
      email: this.regEmail.trim(),
      password: this.regPassword
    }).subscribe({
      next: resp => {
        this.session.iniciarSesion(resp);
        this.cargandoAuth.set(false);
        this.router.navigateByUrl('/panel');
      },
      error: err => {
        this.cargandoAuth.set(false);
        this.errorAuth.set(err.status === 409 ? err.error?.mensaje ?? 'Ya existe una cuenta con esos datos.' : 'No pudimos crear la cuenta.');
      }
    });
  }
}
