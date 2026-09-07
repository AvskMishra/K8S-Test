import { Component, OnInit, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ProductService } from '../../services/product';

@Component({
  selector: 'app-product-form',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './product-form.html',
  styleUrl: './product-form.scss'
})
export class ProductForm implements OnInit {
  private fb = inject(FormBuilder);
  private productService = inject(ProductService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  productId = signal<string | null>(null);
  isEditMode = signal(false);
  saving = signal(false);
  error = signal<string | null>(null);

  form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    description: ['', [Validators.maxLength(1000)]],
    category: ['', [Validators.maxLength(100)]],
    price: [0, [Validators.required, Validators.min(0)]],
    quantity: [0, [Validators.required, Validators.min(0)]]
  });

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (id) {
      this.productId.set(id);
      this.isEditMode.set(true);
      this.productService.getById(id).subscribe({
        next: (product) => {
          this.form.patchValue({
            name: product.name,
            description: product.description ?? '',
            category: product.category ?? '',
            price: product.price,
            quantity: product.quantity
          });
        },
        error: () => this.error.set('Could not load this product.')
      });
    }
  }

  submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    const value = this.form.getRawValue();
    const payload = {
      name: value.name!,
      description: value.description || undefined,
      category: value.category || undefined,
      price: value.price!,
      quantity: value.quantity!
    };

    const onSuccess = () => this.router.navigate(['/']);
    const onError = () => {
      this.saving.set(false);
      this.error.set('Failed to save product. Please check the values and try again.');
    };

    const id = this.productId();
    if (id) {
      this.productService.update(id, payload).subscribe({ next: onSuccess, error: onError });
    } else {
      this.productService.create(payload).subscribe({ next: onSuccess, error: onError });
    }
  }
}
