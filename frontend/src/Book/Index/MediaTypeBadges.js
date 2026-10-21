import PropTypes from 'prop-types';
import React from 'react';

// Talearr: format badges for the All formats view, dimmed when that format has no file
function MediaTypeBadges({ className, missingClassName, formats }) {
  return (
    <span className={className}>
      {
        formats.map(({ mediaType, hasFile }) => {
          const label = mediaType === 'ebook' ? 'eBook' : 'Audiobook';

          return (
            <span
              key={mediaType}
              className={hasFile ? undefined : missingClassName}
              title={`${label}: ${hasFile ? 'downloaded' : 'missing'}`}
            >
              {mediaType === 'ebook' ? '📖' : '🎧'}
            </span>
          );
        })
      }
    </span>
  );
}

MediaTypeBadges.propTypes = {
  className: PropTypes.string,
  missingClassName: PropTypes.string,
  formats: PropTypes.arrayOf(PropTypes.shape({
    mediaType: PropTypes.string.isRequired,
    hasFile: PropTypes.bool.isRequired
  })).isRequired
};

export default MediaTypeBadges;
