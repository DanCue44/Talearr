import ModelBase from 'App/ModelBase';

export type AuthorStatus = 'continuing' | 'ended';

interface Author extends ModelBase {
  added: string;
  genres: string[];
  monitored: boolean;
  overview: string;
  path: string;
  audiobookQualityProfileId?: number;
  ebookQualityProfileId?: number;
  metadataProfileId: number;
  audiobookMetadataProfileId?: number;
  ebookMetadataProfileId?: number;
  audiobookRootFolderPath?: string;
  ebookRootFolderPath?: string;
  sortName: string;
  status: AuthorStatus;
  tags: number[];
  authorName: string;
  lastSelectedMediaType?: string;
  // Talearr: 'full' (all books by the author) or 'tracked' (only the user's books).
  talearrCatalogMode?: 'full' | 'tracked';
  isSaving?: boolean;
}

export default Author;
