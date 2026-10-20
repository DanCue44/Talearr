// Talearr: turn a release's rejections into { label, reason } pairs for interactive search.
// rejectionDetails carries a short label from the server ("Language", "Wrong book", "Pack");
// plain rejection strings (older servers, or details missing) fall back to a generic label.
export default function getRejectionDisplay(rejections, rejectionDetails) {
  const items = [];
  const seen = new Set();

  const add = (label, reason) => {
    const text = (reason ?? '').toString().trim();

    if (!text) {
      return;
    }

    const key = text.toLowerCase();

    if (seen.has(key)) {
      return;
    }

    seen.add(key);
    items.push({
      label: (label ?? '').toString().trim() || 'Rejected',
      reason: text
    });
  };

  (rejectionDetails || []).forEach((detail) => {
    if (detail) {
      add(detail.label, detail.reason);
    }
  });

  (rejections || []).forEach((reason) => add(null, reason));

  return items;
}
