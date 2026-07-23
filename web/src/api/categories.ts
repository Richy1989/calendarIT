import { api } from './client'
import type { components } from './schema'

export type CategoryDto = components['schemas']['CategoryDto']

export async function listCategories(): Promise<CategoryDto[]> {
  const { data, error } = await api.GET('/api/categories')
  if (error || !data) throw new Error('Failed to load categories')
  return data
}

export async function createCategory(name: string, color: string): Promise<CategoryDto> {
  const { data, error, response } = await api.POST('/api/categories', { body: { name, color } })
  if (error || !data) {
    throw new Error(response?.status === 409 ? 'A category with that name already exists.' : 'Failed to create category')
  }
  return data
}

export async function updateCategory(id: string, name: string, color: string): Promise<CategoryDto> {
  const { data, error, response } = await api.PUT('/api/categories/{id}', {
    params: { path: { id } },
    body: { name, color },
  })
  if (error || !data) {
    throw new Error(response?.status === 409 ? 'A category with that name already exists.' : 'Failed to update category')
  }
  return data
}

/** Deletes a category; its appointments keep existing but lose the color (uncategorized). */
export async function deleteCategory(id: string): Promise<void> {
  const { error } = await api.DELETE('/api/categories/{id}', { params: { path: { id } } })
  if (error) throw new Error('Failed to delete category')
}
