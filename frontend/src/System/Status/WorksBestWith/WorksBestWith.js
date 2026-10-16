import React from 'react';
import FieldSet from 'Components/FieldSet';
import styles from './WorksBestWith.css';

// TaleArr: companion apps shown on System > Status.
const companions = [
  {
    name: 'AllSeerr',
    description: 'Request movies, series, music, books and audiobooks.',
    url: 'https://allseerr.com',
    image: 'works-best-with-allseerr.png'
  },
  {
    name: 'NoBookEnds',
    description: 'Listen and read your Audiobookshelf library on iPhone and Android.',
    url: 'https://nobookends.com',
    image: 'works-best-with-nobookends.png'
  }
];

function WorksBestWith() {
  return (
    <FieldSet legend="Works best with">
      <div className={styles.cards}>
        {
          companions.map((companion) => {
            return (
              <a
                key={companion.name}
                className={styles.card}
                href={companion.url}
                target="_blank"
                rel="noopener noreferrer"
              >
                <img
                  className={styles.logo}
                  src={`${window.Chaptarr.urlBase}/Content/Images/${companion.image}`}
                  alt=""
                />
                <div className={styles.text}>
                  <div className={styles.name}>{companion.name}</div>
                  <div className={styles.description}>{companion.description}</div>
                  <div className={styles.url}>{companion.url.replace('https://', '')}</div>
                </div>
              </a>
            );
          })
        }
      </div>
    </FieldSet>
  );
}

export default WorksBestWith;
