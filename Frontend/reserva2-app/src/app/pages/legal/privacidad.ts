import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-privacidad',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './privacidad.html',
  styleUrls: ['./legal.css']
})
export class Privacidad {}
