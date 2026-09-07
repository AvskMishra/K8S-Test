import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { ClusterList } from './cluster-list';

describe('ClusterList', () => {
  let component: ClusterList;
  let fixture: ComponentFixture<ClusterList>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ClusterList],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])]
    }).compileComponents();

    fixture = TestBed.createComponent(ClusterList);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
