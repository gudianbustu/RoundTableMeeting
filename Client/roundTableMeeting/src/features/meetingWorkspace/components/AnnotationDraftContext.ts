import { createContext } from 'react'

export const AnnotationDraftContext = createContext<((file: File) => boolean) | undefined>(undefined)
