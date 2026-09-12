import { Component, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';

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

  constructor(private router: Router) {}

  @HostListener('window:scroll', [])
  onWindowScroll() {
    this.isScrolled = window.scrollY > 50;
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
}
