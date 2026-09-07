import { Component, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CurrencyPipe } from '@angular/common';
import { ProductService } from '../../services/product';
import { Product } from '../../models/product.model';

@Component({
  selector: 'app-product-list',
  imports: [RouterLink, CurrencyPipe],
  templateUrl: './product-list.html',
  styleUrl: './product-list.scss'
})
export class ProductList implements OnInit {
  products = signal<Product[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);

  constructor(private productService: ProductService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.productService.getAll().subscribe({
      next: (products) => {
        this.products.set(products);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not load products. Is the API running at localhost:8080?');
        this.loading.set(false);
      }
    });
  }

  deleteProduct(product: Product): void {
    if (!product.id) return;
    if (!confirm(`Delete "${product.name}"? This cannot be undone.`)) return;

    this.productService.delete(product.id).subscribe({
      next: () => this.products.update((list) => list.filter((p) => p.id !== product.id)),
      error: () => this.error.set('Failed to delete product.')
    });
  }
}
