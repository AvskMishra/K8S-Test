export interface Product {
  id?: string;
  name: string;
  description?: string;
  category?: string;
  price: number;
  quantity: number;
}

// Shape the API accepts for create/update (no id).
export type ProductInput = Omit<Product, 'id'>;
