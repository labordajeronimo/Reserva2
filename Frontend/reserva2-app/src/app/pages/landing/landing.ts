import { Component, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { linkWhatsApp } from '../../core/whatsapp';

@Component({
  selector: 'app-landing',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './landing.html',
  styleUrls: ['./landing.css']
})
export class Landing {
  isScrolled = false;
  facturacionAnual = false;
  menuMobileAbierto = false;

  private tickeando = false;

  constructor(private router: Router) {}

  @HostListener('window:scroll', [])
  onWindowScroll() {
    // Se agrupa con requestAnimationFrame y solo se actualiza si el valor realmente
    // cambió, para no disparar un ciclo de change detection en cada pixel de scroll
    // (eso es lo que trababa el scroll en Safari iOS).
    if (this.tickeando) return;
    this.tickeando = true;
    requestAnimationFrame(() => {
      const scrolled = window.scrollY > 50;
      if (scrolled !== this.isScrolled) this.isScrolled = scrolled;
      this.tickeando = false;
    });
  }

  toggleMenuMobile(): void {
    this.menuMobileAbierto = !this.menuMobileAbierto;
  }

  cerrarMenuMobile(): void {
    this.menuMobileAbierto = false;
  }

  irAlPanel(): void {
    this.router.navigate(['/panel']);
  }

  linkInteresWhatsApp(): string {
    return linkWhatsApp('Hola! Me interesa Reserva2 para mi negocio.');
  }
}
