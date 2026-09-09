import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { Api } from '../../core/api';
import { Session } from '../../core/session';

@Component({
  selector: 'app-super-admin-login',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './super-admin-login.html',
  styleUrls: ['./super-admin-login.css']
})
export class SuperAdminLogin {
  errorAuth = signal<string | null>(null);
  cargandoAuth = signal(false);

  loginEmail = '';
  loginPassword = '';

  constructor(private api: Api, private session: Session, private router: Router) {
    if (this.session.obtenerSuperAdmin()) this.router.navigateByUrl('/super-admin');
  }

  login(): void {
    this.errorAuth.set(null);
    this.cargandoAuth.set(true);
    this.api.superAdminLogin(this.loginEmail.trim(), this.loginPassword).subscribe({
      next: resp => {
        this.session.iniciarSesionSuperAdmin(resp);
        this.cargandoAuth.set(false);
        this.router.navigateByUrl('/super-admin');
      },
      error: () => {
        this.cargandoAuth.set(false);
        this.errorAuth.set('Email o contraseña incorrectos.');
      }
    });
  }
}
