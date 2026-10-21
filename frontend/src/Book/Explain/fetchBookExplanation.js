import createAjaxRequest from 'Utilities/createAjaxRequest';

const CACHE_MS = 30 * 1000;
const cache = new Map();

// Talearr: GET /talearr/explain/{bookId}. History popovers for the same book share one request.
export default function fetchBookExplanation(bookId, { force = false } = {}) {
  const cached = cache.get(bookId);

  if (!force && cached && Date.now() - cached.at < CACHE_MS) {
    return cached.promise;
  }

  const promise = new Promise((resolve, reject) => {
    createAjaxRequest({ url: `/talearr/explain/${bookId}` }).request
      .done(resolve)
      .fail((xhr) => {
        cache.delete(bookId);
        reject(xhr);
      });
  });

  cache.set(bookId, { at: Date.now(), promise });

  return promise;
}
