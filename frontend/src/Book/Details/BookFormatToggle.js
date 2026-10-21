import PropTypes from 'prop-types';
import React from 'react';
import SegmentedToggle from 'Components/Form/SegmentedToggle';
import styles from './BookFormatToggle.css';

// Talearr: an ebook and an audiobook of the same work share one book page; this switches
// the format-specific parts (monitoring, files, search, editions) between the two rows.
function BookFormatToggle({ mediaType, onFormatChange }) {
  const options = [
    {
      key: 'ebook',
      label: 'eBook',
      icon: <span className={styles.icon}>📖</span>
    },
    {
      key: 'audiobook',
      label: 'Audiobook',
      icon: <span className={styles.icon}>🎧</span>
    }
  ];

  return (
    <div className={styles.container}>
      <SegmentedToggle
        value={mediaType === 'ebook' ? 'ebook' : 'audiobook'}
        options={options}
        onChange={onFormatChange}
      />
    </div>
  );
}

BookFormatToggle.propTypes = {
  mediaType: PropTypes.string,
  onFormatChange: PropTypes.func.isRequired
};

export default BookFormatToggle;
